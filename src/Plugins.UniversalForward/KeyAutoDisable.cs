using System.Collections.Concurrent;

namespace Plugins.UniversalForward;

internal sealed record KeyAutoDisablePolicy
{
    public bool Enabled { get; init; }
    public string StatusCodes { get; init; } = "401,403";
    public int FailureThreshold { get; init; } = 3;

    public ForwardRetryRules Rules()
    {
        if (FailureThreshold is < 1 or > 100 || string.IsNullOrWhiteSpace(StatusCodes))
            throw new FormatException("自动禁用阈值须为 1–100，HTTP 状态码不能为空");
        var rules = ForwardRetryRules.Parse(StatusCodes);
        if (Enumerable.Range(100, 300).Any(rules.Contains))
            throw new FormatException("自动禁用仅支持 400–599 的 HTTP 状态码或区间");
        return rules;
    }
}

/// <summary>进程内连续失败计数；凭据中的启用状态仍是持久化事实来源。</summary>
internal sealed class KeyHealthTracker
{
    private readonly ConcurrentDictionary<(string Channel, string Key), Entry> _entries = new();
    internal sealed class Entry(ChannelKey key)
    {
        public object Gate { get; } = new();
        public ChannelKey Key { get; } = key;
        public int Failures { get; set; }
        public ChannelKey? PendingDisable { get; set; }
        public bool Blocked { get; set; }
        public string? SaveError { get; set; }
        public DateTimeOffset NextPruneAt { get; set; }
        public Dictionary<string, DateTimeOffset> FailedRequests { get; } = new(StringComparer.Ordinal);
    }

    internal static bool SameGeneration(ChannelKey a, ChannelKey b)
        => a.HealthVersion == b.HealthVersion && a.Secret == b.Secret;

    public bool HasChannel(string channel) => _entries.Keys.Any(k => k.Channel == channel);
    public bool HasPending(string channel) => _entries.Any(p => p.Key.Channel == channel && p.Value.PendingDisable is not null);

    public Entry For(string channel, ChannelKey key)
        => _entries.AddOrUpdate((channel, key.Id), _ => new Entry(key),
            (_, old) => SameGeneration(old.Key, key) ? old : new Entry(key));

    public bool IsBlocked(string channel, ChannelKey key, string? requestId = null)
    {
        if (!_entries.TryGetValue((channel, key.Id), out var entry) || !SameGeneration(entry.Key, key)) return false;
        lock (entry.Gate)
            return entry.Blocked || requestId is not null && entry.FailedRequests.ContainsKey(requestId);
    }

    public void Observe(string channel, ChannelKey key, string requestId, bool matched, int status,
        KeyAutoDisablePolicy policy, string source)
    {
        var entry = For(channel, key);
        lock (entry.Gate)
        {
            if (entry.Blocked) return;
            var now = DateTimeOffset.UtcNow;
            if (now >= entry.NextPruneAt)
            {
                foreach (var id in entry.FailedRequests.Where(p => now - p.Value > TimeSpan.FromHours(2)).Select(p => p.Key).ToArray())
                    entry.FailedRequests.Remove(id);
                entry.NextPruneAt = now.AddMinutes(1);
            }
            if (!matched) { entry.Failures = 0; return; }
            if (entry.FailedRequests.ContainsKey(requestId)) return;
            entry.FailedRequests[requestId] = now;
            entry.Failures++;
            if (entry.Failures < policy.FailureThreshold) return;
            entry.Blocked = true;
            entry.PendingDisable = key with
            {
                Enabled = false, AutoDisabledAt = now, AutoDisabledStatusCode = status,
                AutoDisabledSource = source, AutoDisabledFailures = entry.Failures
            };
        }
    }

    public object Describe(string channel, ChannelKey key)
    {
        if (!_entries.TryGetValue((channel, key.Id), out var entry) || !SameGeneration(entry.Key, key))
            return new { consecutiveFailures = key.AutoDisabledFailures, saveError = (string?)null };
        lock (entry.Gate) return new { consecutiveFailures = entry.Failures, saveError = entry.SaveError };
    }

    public ChannelKey Display(string channel, ChannelKey key)
    {
        if (!_entries.TryGetValue((channel, key.Id), out var entry) || !SameGeneration(entry.Key, key)) return key;
        lock (entry.Gate) return entry.PendingDisable ?? key;
    }

    public void Synchronize(string channel, IReadOnlyList<ChannelKey> keys)
    {
        foreach (var pair in _entries.Where(p => p.Key.Channel == channel))
        {
            var key = keys.FirstOrDefault(k => k.Id == pair.Key.Key);
            if (key is null || !SameGeneration(pair.Value.Key, key)) _entries.TryRemove(pair.Key, out _);
        }
    }

    public void Remove(string channel)
    {
        foreach (var key in _entries.Keys.Where(k => k.Channel == channel)) _entries.TryRemove(key, out _);
    }
    public void Clear() => _entries.Clear();
}
