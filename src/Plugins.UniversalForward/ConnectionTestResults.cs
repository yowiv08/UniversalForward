using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>测试任务只向宿主报告小型进度；正文在独立、有界的运行内存中按需读取。</summary>
internal sealed class ConnectionTestResults(long capacityBytes = 64L * 1024 * 1024) : IDisposable
{
    internal const int BodyLimit = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] InitialNameFields = ["keyName", "initialKeyName"];
    private readonly object _gate = new();
    private readonly Dictionary<string, Batch> _batches = new(StringComparer.Ordinal);
    private long _bodyBytes;
    private long _sequence;
    private sealed class Batch(List<JsonObject> rows)
    {
        public List<JsonObject> Rows { get; } = rows;
        public Dictionary<int, Body> Bodies { get; } = [];
        public DateTimeOffset? FinishedAt { get; set; }
        public int Version { get; set; }
    }
    private sealed record Body(byte[] Bytes, long Sequence);

    public void Begin(string id, IEnumerable<object> rows)
    {
        lock (_gate)
        {
            Prune();
            Remove(id);
            _batches[id] = new Batch(rows.Select((row, index) =>
            {
                var json = JsonSerializer.SerializeToNode(row, Json)!.AsObject();
                json["rowId"] = index; json["jobId"] = id; json["state"] = "queued";
                foreach (var name in InitialNameFields)
                    if (json[name]?.GetValue<string>() is { Length: > 1024 } value) json[name] = value[..1024] + "…";
                return json;
            }).ToList());
        }
    }

    public void Running(string id, int row)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(id, out var batch)) return;
            batch.Rows[row]["state"] = "running"; batch.Version++;
        }
    }

    public void Set(string id, int row, object result, byte[]? bytes = null)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(id, out var batch)) return;
            var json = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
            json.Remove("response");
            foreach (var pair in json) batch.Rows[row][pair.Key] = pair.Value?.DeepClone();
            var target = batch.Rows[row];
            if (target["keyName"] is null) target["keyName"] = batch.Rows[row]["initialKeyName"]?.DeepClone();
            target.Remove("initialKeyName");
            foreach (var name in new[] { "keyName", "error" })
                if (target[name]?.GetValue<string>() is { Length: > 1024 } value) target[name] = value[..1024] + "…";
            target["state"] = target["skipped"]?.GetValue<bool>() == true ? "skipped"
                : target["success"]?.GetValue<bool>() == true ? "succeeded" : "failed";
            if (bytes is { Length: > 0 })
            {
                var length = (int)Math.Min(Math.Min(bytes.Length, BodyLimit), capacityBytes);
                while (_bodyBytes + length > capacityBytes)
                {
                    var oldest = _batches.SelectMany(b => b.Value.Bodies.Select(p => (Batch: b.Value, Row: p.Key, Body: p.Value)))
                        .MinBy(p => p.Body.Sequence);
                    _bodyBytes -= oldest.Body.Bytes.Length;
                    oldest.Batch.Bodies.Remove(oldest.Row);
                    oldest.Batch.Rows[oldest.Row]["responseEvicted"] = true;
                    oldest.Batch.Version++;
                }
                if (batch.Bodies.Remove(row, out var old)) _bodyBytes -= old.Bytes.Length;
                batch.Bodies[row] = new Body(bytes.AsSpan(0, length).ToArray(), ++_sequence);
                _bodyBytes += length;
                target["responseAvailable"] = true;
                if (length < bytes.Length) target["responseTruncated"] = true;
            }
            batch.Version++;
        }
    }

    public object Progress(string id, int? runningIndex = null)
    {
        lock (_gate)
        {
            var batch = _batches[id];
            return new { completed = batch.Rows.Count(r => r["state"]!.GetValue<string>() is not ("queued" or "running")),
                total = batch.Rows.Count, runningIndex, resultVersion = batch.Version };
        }
    }

    public void Finish(string id, bool cancelled = false, string? error = null)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(id, out var batch)) return;
            foreach (var row in batch.Rows.Where(r => r["state"]!.GetValue<string>() is "queued" or "running"))
            {
                row["state"] = cancelled ? "cancelled" : "notExecuted";
                row["cancelled"] = cancelled;
                row["error"] = error is { Length: > 1024 } ? error[..1024] : error ?? (cancelled ? "测试已取消" : "测试未执行");
            }
            batch.FinishedAt = DateTimeOffset.UtcNow; batch.Version++; Prune();
        }
    }

    public object? Page(string id, int offset, int limit)
    {
        lock (_gate)
        {
            Prune();
            if (!_batches.TryGetValue(id, out var batch)) return null;
            var rows = batch.Rows.Skip(offset).Take(Math.Clamp(limit, 1, 20)).Select(r => r.DeepClone()).ToArray();
            return new { rows, total = batch.Rows.Count, nextOffset = offset + rows.Length, resultVersion = batch.Version };
        }
    }

    public object? Detail(string id, int row, int offset)
    {
        lock (_gate)
        {
            Prune();
            if (!_batches.TryGetValue(id, out var batch) || row < 0 || row >= batch.Rows.Count) return null;
            if (!batch.Bodies.TryGetValue(row, out var body))
                return new { text = "", nextOffset = 0, done = true, evicted = batch.Rows[row]["responseEvicted"]?.GetValue<bool>() == true,
                    truncated = batch.Rows[row]["responseTruncated"]?.GetValue<bool>() == true };
            offset = Math.Clamp(offset, 0, body.Bytes.Length);
            var end = Math.Min(offset + 64 * 1024, body.Bytes.Length);
            while (end < body.Bytes.Length && end > offset && (body.Bytes[end] & 0xc0) == 0x80) end--;
            if (end == offset && offset < body.Bytes.Length) end = Math.Min(offset + 64 * 1024, body.Bytes.Length);
            return new { text = Encoding.UTF8.GetString(body.Bytes, offset, end - offset), nextOffset = end,
                done = end == body.Bytes.Length, evicted = false, truncated = batch.Rows[row]["responseTruncated"]?.GetValue<bool>() == true };
        }
    }

    private void Prune()
    {
        foreach (var item in _batches.Where(p => p.Value.FinishedAt is not null).OrderByDescending(p => p.Value.FinishedAt)
            .Where((p, index) => index >= 128 || p.Value.FinishedAt < DateTimeOffset.UtcNow.AddHours(-1)).ToArray()) Remove(item.Key);
    }
    private void Remove(string id)
    {
        if (_batches.Remove(id, out var batch)) _bodyBytes -= batch.Bodies.Values.Sum(b => (long)b.Bytes.Length);
    }
    public void Dispose() { lock (_gate) { _batches.Clear(); _bodyBytes = 0; } }
}
