using System.Collections.Concurrent;

namespace Plugins.UniversalForward;
/// <summary>渠道 Key 的名称、密钥和启用状态。</summary>
internal sealed record ChannelKey
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Secret { get; init; } = "";
    public bool Enabled { get; init; } = true;
}
/// <summary>Key 列表校验与编辑。</summary>
internal static class ChannelKeys
{
    public static List<ChannelKey> Read(List<ChannelKey>? keys, string legacy)
        => keys ?? (string.IsNullOrWhiteSpace(legacy) ? [] :
            [new ChannelKey { Id = "legacy", Name = "默认 Key", Secret = legacy }]);

    public static void Validate(IReadOnlyList<ChannelKey> keys, string mode)
    {
        if (mode is not ("roundRobin" or "random" or "priority"))
            throw new FormatException("Key 分配策略无效");
        if (keys.Count > 100) throw new FormatException("每渠道最多 100 个 Key");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.Id) || !ids.Add(key.Id))
                throw new FormatException("Key ID 不能为空或重复");
            if (string.IsNullOrWhiteSpace(key.Secret) || key.Secret.Any(char.IsControl))
                throw new FormatException("Key 不能为空或包含控制字符");
            if (!secrets.Add(key.Secret)) throw new FormatException("渠道内存在重复 Key");
        }
    }

    public static List<ChannelKey> Merge(IReadOnlyList<ChannelKey> previous,
        IReadOnlyList<ChannelKey>? requested, IReadOnlyList<string>? deleted)
    {
        var old = previous.ToDictionary(k => k.Id, StringComparer.Ordinal);
        var remove = new HashSet<string>(deleted ?? [], StringComparer.Ordinal);
        if (remove.Any(id => string.IsNullOrWhiteSpace(id) || !old.ContainsKey(id)))
            throw new FormatException("删除的 Key ID 不能为空且必须属于此渠道");
        var result = new List<ChannelKey>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in requested ?? [])
        {
            if (item is null) throw new FormatException("Key 配置无效");
            var id = item.Id;
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(item.Secret)) throw new FormatException("新增 Key 必须填写密钥");
            }
            else if (!old.ContainsKey(id)) throw new FormatException("Key ID 不属于此渠道");
            if (!seen.Add(id) || remove.Contains(id)) throw new FormatException("Key ID 重复或同时提交删除");
            var secret = string.IsNullOrWhiteSpace(item.Secret) ? old[id].Secret : item.Secret.Trim();
            result.Add(item with { Id = id, Name = item.Name?.Trim() ?? "", Secret = secret });
        }
        result.AddRange(previous.Where(k => !seen.Contains(k.Id) && !remove.Contains(k.Id)));
        return result;
    }
}
/// <summary>轮询、随机和主备顺序分配。</summary>
internal sealed class ChannelKeySelector
{
    private readonly ConcurrentDictionary<string, Cursor> _cursors = new(StringComparer.Ordinal);

    public ChannelKey Select(string channel, IReadOnlyList<ChannelKey> keys, string mode, string? specified = null,
        bool discovery = false)
    {
        var enabled = keys.Where(k => k.Enabled).ToArray();
        if (specified is not null)
            return enabled.FirstOrDefault(k => k.Id == specified)
                ?? throw new FormatException("指定 Key 已删除、停用或不属于此渠道");
        if (enabled.Length == 0) throw new FormatException("渠道没有启用的 Key");
        if (discovery || mode == "priority") return enabled[0];
        if (mode == "random") return enabled[Random.Shared.Next(enabled.Length)];
        if (mode != "roundRobin") throw new FormatException("Key 分配策略无效");
        var cursor = _cursors.GetOrAdd(channel, _ => new Cursor());
        lock (cursor)
        {
            var signature = System.Text.Json.JsonSerializer.Serialize(keys.Select(k => new { k.Id, k.Enabled }));
            if (signature != cursor.Signature) { cursor.Signature = signature; cursor.Next = 0; }
            var selected = enabled[cursor.Next];
            cursor.Next = (cursor.Next + 1) % enabled.Length;
            return selected;
        }
    }

    public void Remove(string channel) => _cursors.TryRemove(channel, out _);
    public void Clear() => _cursors.Clear();

    private sealed class Cursor
    {
        public string Signature { get; set; } = "";
        public int Next { get; set; }
    }
}
