using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Plugins.UniversalForward;

internal sealed record RequestLogSettings(bool Enabled = true, long CapacityBytes = 64L * 1024 * 1024,
    int BodyLimitBytes = 4 * 1024 * 1024, int MaxRecords = 1000)
{
    public void Validate()
    {
        if (CapacityBytes is < 1024 * 1024 or > 512L * 1024 * 1024
            || BodyLimitBytes is < 1024 or > 32 * 1024 * 1024 || MaxRecords is < 1 or > 10000)
            throw new FormatException("正文容量须为 1–512 MiB，单向上限须为 1 KiB–32 MiB，记录数须为 1–10000");
    }
}

internal sealed class RequestLogStore : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _gaps = new(StringComparer.Ordinal);
    private readonly Channel<Action<RequestLogStore>> _queue;
    private readonly Task _writer;
    private RequestLogSettings _settings = new();
    private long _bodyBytes, _dropped;
    private string? _error;
    private int _disposed;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public bool Available => Volatile.Read(ref _disposed) == 0 && !_writer.IsCompleted;
    public RequestLogSettings Settings => Volatile.Read(ref _settings);
    public object Status
    {
        get { lock (_gate) return new { available = Available, storage = "memory", sessionId = SessionId,
            error = Volatile.Read(ref _error), droppedWrites = Interlocked.Read(ref _dropped),
            bodyBytes = _bodyBytes, records = _entries.Count, settings = Settings }; }
    }
    private sealed class Entry(JsonObject info)
    {
        public JsonObject Info = info;
        public readonly SortedDictionary<int, JsonObject> Attempts = [];
        public readonly Dictionary<string, Part> Parts = new(StringComparer.Ordinal);
        public bool Running => Info["state"]?.ToString() == "running";
    }
    private sealed class Part : IDisposable
    {
        public readonly MemoryStream Body = new();
        public long Observed;
        public bool Truncated;
        public void Dispose() => Body.Dispose();
    }
    public RequestLogStore(int queueCapacity = 256)
    {
        _queue = Channel.CreateBounded<Action<RequestLogStore>>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = Task.Run(WriteLoopAsync);
    }
    public bool Enqueue(string id, Action<RequestLogStore> write)
    {
        if (Available && _queue.Writer.TryWrite(write)) return true;
        Gap(id, "日志队列已满或采集已停止，部分内容未保留");
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
        await foreach (var write in _queue.Reader.ReadAllAsync())
        {
            try { lock (_gate) { write(this); ApplyGaps(); } }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException)
            { Gap("", error.Message); }
        }
    }
    private void ApplyGaps()
    {
        foreach (var id in _gaps.Keys)
            if (_entries.TryGetValue(id, out var entry)) entry.Info["incomplete"] = true;
    }
    public RequestLogCapture? Begin(string kind, string? channel, string? label, string? model,
        string? endpoint, string? network, string? trace, object? headers, string? body, string? extensions)
    {
        if (!Available || !Settings.Enabled) return null;
        var capture = new RequestLogCapture(this, kind, channel, label, model, endpoint, network, trace, headers);
        capture.AddText("incoming", body ?? ""); capture.AddText("extensions", extensions ?? ""); capture.Save();
        return capture;
    }
    public async Task FlushAsync(CancellationToken token = default)
    {
        if (!Available) throw new IOException("本次运行日志已停止");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(store => { store.ApplyGaps(); completion.TrySetResult(); }, token);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
    }
    internal void Insert(string id, string json)
    {
        while (_entries.Count >= Settings.MaxRecords && EvictOldest()) { }
        if (_entries.Count >= Settings.MaxRecords) { Gap(id, "活动日志已达到记录数上限，新日志未采集"); return; }
        _entries[id] = new Entry(JsonNode.Parse(json)!.AsObject());
    }
    internal void Update(string id, string json)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        var incomplete = entry.Info["incomplete"]?.GetValue<bool>() ?? false;
        entry.Info = JsonNode.Parse(json)!.AsObject();
        if (incomplete) entry.Info["incomplete"] = true;
    }
    internal void Attempt(string id, int number, string json)
    {
        if (_entries.TryGetValue(id, out var entry)) entry.Attempts[number] = JsonNode.Parse(json)!.AsObject();
    }
    private static Part GetPart(Entry entry, string name)
    {
        if (!entry.Parts.TryGetValue(name, out var part)) entry.Parts[name] = part = new Part();
        return part;
    }
    internal void EndPart(string id, string name, long observed, bool truncated)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        var part = GetPart(entry, name); part.Observed = observed; part.Truncated |= truncated;
        if (part.Truncated) entry.Info["incomplete"] = true;
    }
    internal void AppendChunk(string id, string name, long observed, byte[] chunk)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        var part = GetPart(entry, name); part.Observed = observed;
        while (_bodyBytes + chunk.Length > Settings.CapacityBytes && EvictOldest()) { }
        if (_bodyBytes + chunk.Length > Settings.CapacityBytes)
        {
            part.Truncated = true; entry.Info["incomplete"] = true;
            Gap(id, "活动日志已达到正文容量上限，部分内容未保留"); return;
        }
        part.Body.Write(chunk); _bodyBytes += chunk.Length;
    }
    private bool EvictOldest()
    {
        var oldest = _entries.Where(p => !p.Value.Running)
            .OrderBy(p => p.Value.Info["started"]!.GetValue<long>()).FirstOrDefault();
        if (oldest.Key is null) return false;
        Remove(oldest.Key); return true;
    }
    private void Remove(string id)
    {
        if (!_entries.Remove(id, out var entry)) return;
        foreach (var part in entry.Parts.Values) { _bodyBytes -= part.Body.Length; part.Dispose(); }
        _gaps.TryRemove(id, out _);
    }
    internal void Cleanup()
    {
        while ((_bodyBytes > Settings.CapacityBytes || _entries.Count > Settings.MaxRecords) && EvictOldest()) { }
    }
    public object List(IReadOnlyDictionary<string, string> query)
    {
        lock (_gate)
        {
            IEnumerable<Entry> filtered = _entries.Values;
            foreach (var name in new[] { "channel", "model", "endpoint", "kind", "network", "trace", "state", "status" })
                if (query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                    filtered = filtered.Where(e => e.Info[name]?.ToString() == value);
            if (query.TryGetValue("from", out var from) && DateTimeOffset.TryParse(from, out var start))
                filtered = filtered.Where(e => e.Info["started"]!.GetValue<long>() >= start.ToUnixTimeMilliseconds());
            if (query.TryGetValue("to", out var to) && DateTimeOffset.TryParse(to, out var end))
                filtered = filtered.Where(e => e.Info["started"]!.GetValue<long>() <= end.ToUnixTimeMilliseconds());
            var page = query.TryGetValue("page", out var p) && int.TryParse(p, out var n) ? Math.Clamp(n, 1, 1000000) : 1;
            var size = query.TryGetValue("pageSize", out var s) && int.TryParse(s, out n) ? Math.Clamp(n, 1, 100) : 25;
            var ordered = filtered.OrderByDescending(e => e.Info["started"]!.GetValue<long>())
                .ThenBy(e => e.Info["id"]!.ToString(), StringComparer.Ordinal).ToArray();
            return new { rows = ordered.Skip((page - 1) * size).Take(size).Select(e => e.Info.DeepClone()).ToArray(),
                total = (long)ordered.Length, page, pageSize = size };
        }
    }
    public JsonObject? Detail(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)) return null;
            var row = entry.Info.DeepClone().AsObject();
            row["attempts"] = new JsonArray(entry.Attempts.Values.Select(a => a.DeepClone()).ToArray());
            row["parts"] = new JsonArray(entry.Parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Key, ["observedBytes"] = p.Value.Observed,
                ["savedBytes"] = p.Value.Body.Length, ["truncated"] = p.Value.Truncated
            }).ToArray());
            return row;
        }
    }
    public object ReadPart(string id, string part, int after)
    {
        lock (_gate)
        {
            var chunks = new List<object>(); var cursor = after;
            if (_entries.TryGetValue(id, out var entry) && entry.Parts.TryGetValue(part, out var content))
            {
                var buffer = content.Body.GetBuffer(); var offset = (long)after + 1;
                while (chunks.Count < 4 && offset < content.Body.Length)
                {
                    var count = (int)Math.Min(65536, content.Body.Length - offset);
                    cursor = (int)offset + count - 1;
                    chunks.Add(new { sequence = cursor, base64 = Convert.ToBase64String(buffer, (int)offset, count) });
                    offset += count;
                }
            }
            return new { chunks, next = cursor, done = chunks.Count < 4 };
        }
    }
    public async Task DeleteAsync(string? id, CancellationToken token)
    {
        await FlushAsync(token);
        lock (_gate)
            foreach (var key in _entries.Where(p => !p.Value.Running && (id is null || p.Key == id)).Select(p => p.Key).ToArray()) Remove(key);
    }
    public async Task ConfigureAsync(RequestLogSettings settings, CancellationToken token)
    {
        settings.Validate(); await FlushAsync(token);
        lock (_gate) { Volatile.Write(ref _settings, settings); Cleanup(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete(); _writer.GetAwaiter().GetResult();
        lock (_gate) { foreach (var id in _entries.Keys.ToArray()) Remove(id); _gaps.Clear(); }
    }
}
