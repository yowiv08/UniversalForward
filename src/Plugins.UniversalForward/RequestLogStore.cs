using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Plugins.UniversalForward;

internal sealed record RequestLogSettings(bool Enabled = true, int RetentionDays = 7,
    long CapacityBytes = 5L * 1024 * 1024 * 1024, int BodyLimitBytes = 32 * 1024 * 1024)
{
    public void Validate()
    {
        if (RetentionDays is < 1 or > 3650 || CapacityBytes is < 1024 * 1024 or > 1024L * 1024 * 1024 * 1024
            || BodyLimitBytes is < 1024 or > 128 * 1024 * 1024)
            throw new FormatException("保留天数须为 1–3650，容量须为 1 MiB–1 TiB，单向正文上限须为 1 KiB–128 MiB");
    }
}

internal sealed class RequestLogStore : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<Action<SqliteConnection>> _queue;
    private readonly ConcurrentDictionary<string, byte> _gaps = new(StringComparer.Ordinal);
    private readonly string _connectionString;
    private readonly FileStream? _lease;
    private readonly Task _writer;
    private RequestLogSettings _settings = new();
    private long _dropped;
    private string? _error;
    private int _disposed;
    public string DirectoryPath { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public bool Available { get; }
    public RequestLogSettings Settings => Volatile.Read(ref _settings);
    public object Status => new { available = Available && _disposed == 0 && !_writer.IsCompleted, error = Volatile.Read(ref _error),
        droppedWrites = Interlocked.Read(ref _dropped), directory = DirectoryPath, sessionId = SessionId, settings = Settings };

    public RequestLogStore(string directory, int queueCapacity = 256)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "requests.db"), Pooling = false,
            ForeignKeys = true, DefaultTimeout = 5
        }.ToString();
        _queue = Channel.CreateBounded<Action<SqliteConnection>>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
            else Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _lease = new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var connection = Open();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(DirectoryPath, "requests.db"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (var version = Command(connection, "PRAGMA user_version"))
                if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 1)
                    throw new FormatException("日志数据库版本高于当前插件支持版本");
            Exec(connection, """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK(id=1), value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS requests (
                  id TEXT PRIMARY KEY, session TEXT NOT NULL, started INTEGER NOT NULL,
                  channel TEXT, model TEXT, endpoint TEXT, kind TEXT, network TEXT, trace TEXT,
                  state TEXT NOT NULL, status INTEGER, incomplete INTEGER NOT NULL DEFAULT 0, info TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS request_time ON requests(started DESC,id);
                CREATE INDEX IF NOT EXISTS request_channel ON requests(channel,started DESC);
                CREATE INDEX IF NOT EXISTS request_trace ON requests(trace);
                CREATE TABLE IF NOT EXISTS attempts (
                  request TEXT NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
                  number INTEGER NOT NULL, info TEXT NOT NULL, PRIMARY KEY(request,number));
                CREATE TABLE IF NOT EXISTS parts (
                  request TEXT NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
                  name TEXT NOT NULL, observed INTEGER NOT NULL DEFAULT 0, saved INTEGER NOT NULL DEFAULT 0,
                  truncated INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(request,name));
                CREATE TABLE IF NOT EXISTS chunks (
                  request TEXT NOT NULL, part TEXT NOT NULL, sequence INTEGER NOT NULL, bytes BLOB NOT NULL,
                  PRIMARY KEY(request,part,sequence),
                  FOREIGN KEY(request,part) REFERENCES parts(request,name) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS accounting(id INTEGER PRIMARY KEY CHECK(id=1),bytes INTEGER NOT NULL);
                INSERT OR IGNORE INTO accounting VALUES(1,0);
                CREATE TRIGGER IF NOT EXISTS parts_add AFTER INSERT ON parts BEGIN
                  UPDATE accounting SET bytes=bytes+NEW.saved WHERE id=1; END;
                CREATE TRIGGER IF NOT EXISTS parts_change AFTER UPDATE OF saved ON parts BEGIN
                  UPDATE accounting SET bytes=bytes+NEW.saved-OLD.saved WHERE id=1; END;
                CREATE TRIGGER IF NOT EXISTS parts_remove AFTER DELETE ON parts BEGIN
                  UPDATE accounting SET bytes=bytes-OLD.saved WHERE id=1; END;
                UPDATE accounting SET bytes=(SELECT coalesce(sum(saved),0) FROM parts) WHERE id=1;
                PRAGMA user_version=1;
                UPDATE requests SET state='interrupted',incomplete=1 WHERE state='running';
                """);
            using var command = Command(connection, "SELECT value FROM settings WHERE id=1");
            if (command.ExecuteScalar() is string json)
            {
                _settings = JsonSerializer.Deserialize<RequestLogSettings>(json, Json) ?? new();
                _settings.Validate();
            }
            Cleanup(connection);
            Available = true;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or JsonException or FormatException
            or TypeInitializationException or DllNotFoundException or BadImageFormatException)
        { _error = error.Message; _lease?.Dispose(); }
        _writer = Available ? Task.Run(WriteLoopAsync) : Task.CompletedTask;
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    internal static SqliteCommand Command(SqliteConnection connection, string sql, params object?[] args)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return command;
    }
    internal static void Exec(SqliteConnection connection, string sql, params object?[] args)
    { using var command = Command(connection, sql, args); command.ExecuteNonQuery(); }

    public bool Enqueue(string id, Action<SqliteConnection> write)
    {
        if (Available && _disposed == 0 && _queue.Writer.TryWrite(write)) return true;
        Gap(id, "日志写入不可用或队列已满，部分内容未保存");
        return false;
    }
    private void Gap(string id, string error)
    {
        Interlocked.Increment(ref _dropped); Volatile.Write(ref _error, error);
        if (_gaps.Count < 4096) _gaps.TryAdd(id, 0);
    }
    public void RecordFailure(Exception error) => Gap("", error.Message);
    private async Task WriteLoopAsync()
    {
        try
        {
            using var connection = Open();
            var readable = _queue.Reader.WaitToReadAsync().AsTask();
            var cleanupDue = Task.Delay(TimeSpan.FromMinutes(1));
            while (true)
            {
                if (cleanupDue.IsCompleted || await Task.WhenAny(readable, cleanupDue) != readable)
                {
                    try { Cleanup(connection); }
                    catch (SqliteException error) { Gap("", error.Message); }
                    cleanupDue = Task.Delay(TimeSpan.FromMinutes(1));
                    continue;
                }
                if (!await readable) break;
                var batch = new List<Action<SqliteConnection>>();
                while (batch.Count < 128 && _queue.Reader.TryRead(out var write)) batch.Add(write);
                foreach (var write in batch)
                {
                    try { write(connection); }
                    catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
                    {
                        Gap("", error.Message);
                        try { Exec(connection, "UPDATE requests SET incomplete=1 WHERE session=$0", SessionId); }
                        catch (SqliteException) { }
                    }
                }
                foreach (var id in _gaps.Keys)
                {
                    try { Exec(connection, "UPDATE requests SET incomplete=1 WHERE id=$0", id); }
                    catch (SqliteException) { break; }
                }
                readable = _queue.Reader.WaitToReadAsync().AsTask();
            }
            Exec(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        { Gap("", error.Message); }
        finally { _queue.Writer.TryComplete(); _lease?.Dispose(); }
    }

    public RequestLogCapture? Begin(string kind, string? channel, string? label, string? model,
        string? endpoint, string? network, string? trace, object? headers, string? body, string? extensions)
    {
        if (!Settings.Enabled) return null;
        if (!Available) { Gap("", _error ?? "日志存储不可用"); return null; }
        var capture = new RequestLogCapture(this, kind, channel, label, model, endpoint, network, trace, headers);
        capture.AddText("incoming", body ?? "");
        capture.AddText("extensions", extensions ?? "");
        capture.Save();
        return capture;
    }

    public async Task FlushAsync(CancellationToken token = default)
    {
        if (!Available) throw new IOException(_error ?? "日志存储不可用");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(connection =>
        {
            foreach (var id in _gaps.Keys)
                Exec(connection, "UPDATE requests SET incomplete=1 WHERE id=$0", id);
            completion.TrySetResult();
        }, token);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
    }

    public object List(IReadOnlyDictionary<string, string> query)
    {
        using var connection = Open();
        var where = new List<string>();
        var args = new List<object?>();
        void Add(string sql, object value) { where.Add(sql.Replace("?", "$" + args.Count, StringComparison.Ordinal)); args.Add(value); }
        foreach (var name in new[] { "channel", "model", "endpoint", "kind", "network", "trace", "state" })
            if (query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) Add(name + "=?", value);
        if (query.TryGetValue("status", out var status) && int.TryParse(status, out var code)) Add("status=?", code);
        foreach (var (name, op) in new[] { ("from", ">="), ("to", "<=") })
            if (query.TryGetValue(name, out var value) && DateTimeOffset.TryParse(value, out var date))
                Add("started" + op + "?", date.ToUnixTimeMilliseconds());
        var clause = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        using var count = Command(connection, "SELECT count(*) FROM requests" + clause, args.ToArray());
        var total = Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var page = query.TryGetValue("page", out var p) && int.TryParse(p, out var n) ? Math.Clamp(n, 1, 1000000) : 1;
        var size = query.TryGetValue("pageSize", out var s) && int.TryParse(s, out n) ? Math.Clamp(n, 1, 100) : 25;
        args.Add(size); args.Add((page - 1) * size);
        using var command = Command(connection, "SELECT info,state,status,incomplete FROM requests" + clause +
            $" ORDER BY started DESC,id LIMIT ${args.Count - 2} OFFSET ${args.Count - 1}", args.ToArray());
        using var reader = command.ExecuteReader();
        var rows = new List<JsonObject>();
        while (reader.Read())
        {
            var row = JsonNode.Parse(reader.GetString(0))!.AsObject();
            row["state"] = reader.GetString(1); row["status"] = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            row["incomplete"] = reader.GetInt32(3) != 0;
            rows.Add(row);
        }
        return new { rows, total, page, pageSize = size };
    }

    public JsonObject? Detail(string id)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT info,state,status,incomplete FROM requests WHERE id=$0", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = JsonNode.Parse(reader.GetString(0))!.AsObject();
        row["state"] = reader.GetString(1); row["status"] = reader.IsDBNull(2) ? null : reader.GetInt32(2);
        row["incomplete"] = reader.GetInt32(3) != 0; reader.Close();
        var attempts = new JsonArray();
        using var attemptsCommand = Command(connection, "SELECT info FROM attempts WHERE request=$0 ORDER BY number", id);
        using (var attemptReader = attemptsCommand.ExecuteReader())
            while (attemptReader.Read()) attempts.Add(JsonNode.Parse(attemptReader.GetString(0)));
        row["attempts"] = attempts;
        var parts = new JsonArray();
        using var partsCommand = Command(connection, "SELECT name,observed,saved,truncated FROM parts WHERE request=$0 ORDER BY name", id);
        using (var partReader = partsCommand.ExecuteReader())
            while (partReader.Read()) parts.Add(new JsonObject
            {
                ["name"] = partReader.GetString(0), ["observedBytes"] = partReader.GetInt64(1),
                ["savedBytes"] = partReader.GetInt64(2), ["truncated"] = partReader.GetInt32(3) != 0
            });
        row["parts"] = parts;
        return row;
    }

    public object ReadPart(string id, string part, int after)
    {
        using var connection = Open();
        using var command = Command(connection,
            "SELECT sequence,bytes FROM chunks WHERE request=$0 AND part=$1 AND sequence>$2 ORDER BY sequence LIMIT 4", id, part, after);
        using var reader = command.ExecuteReader();
        var chunks = new List<object>(); var cursor = after;
        while (reader.Read())
        {
            cursor = reader.GetInt32(0);
            chunks.Add(new { sequence = cursor, base64 = Convert.ToBase64String((byte[])reader[1]) });
        }
        return new { chunks, next = cursor, done = chunks.Count < 4 };
    }

    public async Task DeleteAsync(string? id, CancellationToken token)
    {
        await FlushAsync(token);
        using var connection = Open();
        if (id is null) Exec(connection, "DELETE FROM requests WHERE state!='running'");
        else Exec(connection, "DELETE FROM requests WHERE id=$0 AND state!='running'", id);
        Exec(connection, "PRAGMA wal_checkpoint(PASSIVE)");
    }
    public async Task ConfigureAsync(RequestLogSettings settings, CancellationToken token)
    {
        settings.Validate(); await FlushAsync(token);
        using var connection = Open();
        Exec(connection, "INSERT OR REPLACE INTO settings VALUES(1,$0)", JsonSerializer.Serialize(settings, Json));
        Volatile.Write(ref _settings, settings); Cleanup(connection);
    }
    internal void AppendChunk(SqliteConnection connection, string id, string name, int sequence, long observed, byte[] chunk)
    {
        using var transaction = connection.BeginTransaction();
        Exec(connection, "INSERT OR IGNORE INTO parts(request,name) VALUES($0,$1)", id, name);
        using var total = Command(connection, "SELECT bytes FROM accounting WHERE id=1");
        var used = Convert.ToInt64(total.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (used + chunk.Length > Settings.CapacityBytes)
        {
            Cleanup(connection, chunk.Length);
            used = Convert.ToInt64(total.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (used + chunk.Length > Settings.CapacityBytes)
        {
            Exec(connection, "UPDATE parts SET observed=$2,truncated=1 WHERE request=$0 AND name=$1", id, name, observed);
            Exec(connection, "UPDATE requests SET incomplete=1 WHERE id=$0", id);
            Gap(id, "日志容量不足，部分正文未保存");
        }
        else
        {
            Exec(connection, "INSERT INTO chunks VALUES($0,$1,$2,$3)", id, name, sequence, chunk);
            Exec(connection, "UPDATE parts SET observed=$2,saved=saved+$3 WHERE request=$0 AND name=$1", id, name, observed, chunk.Length);
        }
        transaction.Commit();
    }
    internal void Cleanup(SqliteConnection connection) => Cleanup(connection, 0);
    private void Cleanup(SqliteConnection connection, int reserve)
    {
        Exec(connection, "DELETE FROM requests WHERE state!='running' AND started<$0",
            DateTimeOffset.UtcNow.AddDays(-Settings.RetentionDays).ToUnixTimeMilliseconds());
        using var total = Command(connection, "SELECT bytes FROM accounting WHERE id=1");
        var bytes = Convert.ToInt64(total.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        while (bytes + reserve > Settings.CapacityBytes)
        {
            using var oldest = Command(connection, """
                SELECT r.id,coalesce(sum(p.saved),0) FROM requests r LEFT JOIN parts p ON r.id=p.request
                WHERE r.state!='running' GROUP BY r.id ORDER BY r.started,r.id LIMIT 1
                """);
            using var reader = oldest.ExecuteReader();
            if (!reader.Read()) { Volatile.Write(ref _error, "活动请求占用已达到日志容量上限"); break; }
            var id = reader.GetString(0); var size = reader.GetInt64(1); reader.Close();
            Exec(connection, "DELETE FROM requests WHERE id=$0", id); bytes -= size;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _writer.GetAwaiter().GetResult(); }
        finally { SqliteConnection.ClearAllPools(); }
    }
}
