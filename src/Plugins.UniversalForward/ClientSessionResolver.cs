using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>仅保存推断会话的身份和最近一次请求的历史摘要。</summary>
internal sealed class ClientSessionResolver(
    int capacity = 10_000, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly TimeSpan _idleTimeout = idleTimeout is { } timeout
        ? timeout > TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(idleTimeout))
        : TimeSpan.FromHours(24);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly LinkedList<Entry> _recent = new();
    private readonly Dictionary<(string Partition, string Tip), HashSet<Entry>> _tips = new();
    private bool _disposed;

    internal int Count { get { lock (_gate) return _recent.Count; } }

    internal ClientSessionResolution Resolve(string accountId, string authenticationDigest, JsonNode? body, string endpoint)
    {
        var history = ConversationFingerprint.Read(body, endpoint);
        var cacheKey = body?["prompt_cache_key"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        var partition = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { accountId, authenticationDigest, cacheKey })));
        lock (_gate)
        {
            if (_disposed) return New("resolver_stopped");
            var now = _time.GetUtcNow();
            while (_recent.First is { } oldest && now - oldest.Value.LastUsed >= _idleTimeout)
                Remove(oldest.Value);
            if (history.Prefixes.Count == 0) return New("missing_history");

            var reason = history.HasReply ? "history_not_found" : "first_turn";
            if (history.HasReply)
            {
                Entry? candidate = null;
                var matchedPrefix = -1;
                for (var i = 0; i < history.Prefixes.Count; i++)
                {
                    if (!_tips.TryGetValue((partition, history.Prefixes[i]), out var matches)) continue;
                    // A shorter current history is still a possible conversation.
                    // Reuse only when the entire candidate set is unambiguous.
                    if (matches.Count != 1 || candidate is not null)
                    {
                        reason = "ambiguous_history";
                        candidate = null;
                        break;
                    }
                    candidate = matches.First();
                    matchedPrefix = i;
                }
                if (candidate is not null)
                {
                    Unindex(candidate);
                    candidate.Tip = history.Prefixes[^1];
                    candidate.LastUsed = now;
                    _recent.Remove(candidate.Node!);
                    _recent.AddLast(candidate.Node!);
                    Index(candidate);
                    return new(candidate.Identity, "history", matchedPrefix == history.Prefixes.Count - 1 ? "same_history" : "history_extended");
                }
            }

            while (_recent.Count >= _capacity) Remove(_recent.First!.Value);
            var resolution = New(reason);
            var created = new Entry(accountId, partition, history.Prefixes[^1], resolution.Identity, now);
            created.Node = _recent.AddLast(created);
            Index(created);
            return resolution;
        }
    }

    internal void RemoveAccount(string accountId)
    {
        lock (_gate)
        {
            var node = _recent.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.AccountId == accountId) Remove(node.Value);
                node = next;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _recent.Clear();
            _tips.Clear();
        }
    }

    private static ClientSessionResolution New(string reason) => new(ClientSessionIdentity.Create(), "new", reason);

    private void Index(Entry entry)
    {
        var key = (entry.Partition, entry.Tip);
        if (!_tips.TryGetValue(key, out var entries)) _tips[key] = entries = [];
        entries.Add(entry);
    }

    private void Unindex(Entry entry)
    {
        var key = (entry.Partition, entry.Tip);
        var entries = _tips[key];
        entries.Remove(entry);
        if (entries.Count == 0) _tips.Remove(key);
    }

    private void Remove(Entry entry)
    {
        Unindex(entry);
        _recent.Remove(entry.Node!);
    }

    private sealed class Entry(string accountId, string partition, string tip, ClientSessionIdentity identity, DateTimeOffset now)
    {
        internal string AccountId { get; } = accountId;
        internal string Partition { get; } = partition;
        internal string Tip { get; set; } = tip;
        internal ClientSessionIdentity Identity { get; } = identity;
        internal DateTimeOffset LastUsed { get; set; } = now;
        internal LinkedListNode<Entry>? Node { get; set; }
    }
}

internal sealed record ClientSessionResolution(ClientSessionIdentity Identity, string Source, string Reason);

internal sealed record ClientSessionIdentity(
    string SessionId, string ThreadId, string WindowId, string InstallationId, string ContextWindowId, string DeviceId)
{
    internal static ClientSessionIdentity Create()
    {
        var session = Guid.NewGuid().ToString();
        return new(session, session, session + ":0", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
    }
}
