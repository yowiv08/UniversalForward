using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Plugins.UniversalForward;

/// <summary>请求头覆盖、变量替换和名称匹配。</summary>
internal static class HeaderOverrides
{
    private const string ClientPrefix = "{client_header:";
    private static bool Rule(string name) => name == "*" || name.StartsWith("re:", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("regex:", StringComparison.OrdinalIgnoreCase);

    private static Regex Pattern(string key)
    {
        var pattern = key[(key.IndexOf(':') + 1)..].Trim();
        if (pattern.Length is 0 or > 512) throw new FormatException("请求头正则长度必须为1–512。");
        try { return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        { throw new FormatException("请求头正则无效或包含不支持的回溯结构。", e); }
    }

    internal static void Validate(JsonObject config)
    {
        if (config.Count > 128) throw new FormatException("请求头规则最多128项。");
        foreach (var (key, node) in config)
        {
            var name = key.Trim();
            if (Rule(name)) { if (name != "*") _ = Pattern(name); continue; }
            if (!ForwardHeaders.SafeName(name) || node is not JsonValue value
                || !value.TryGetValue<string>(out var text) || text.Length > 8192 || text.Any(char.IsControl))
                throw new FormatException("请求头名称无效，或覆盖值不是安全字符串。");
            var trimmed = text.Trim();
            if (trimmed.StartsWith(ClientPrefix, StringComparison.Ordinal))
            {
                var end = trimmed.IndexOf('}');
                if (end != trimmed.Length - 1 || !trimmed.EndsWith('}')
                    || !ForwardHeaders.SafeName(trimmed[ClientPrefix.Length..^1].Trim()))
                    throw new FormatException("client_header 必须占据整个值并指定有效头名。");
            }
        }
    }

    internal static Dictionary<string, string> Resolve(JsonObject config,
        IReadOnlyDictionary<string, string> client, string apiKey, bool channelTest = false)
    {
        Validate(config);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var all = config.Any(p => p.Key.Trim() == "*");
        var patterns = config.Where(p => Rule(p.Key.Trim()) && p.Key.Trim() != "*").Select(p => Pattern(p.Key.Trim())).ToArray();
        var connection = client.Where(p => p.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!channelTest)
            foreach (var (name, text) in client)
            {
                if (!ForwardHeaders.SafeName(name) || connection.Contains(name)
                    || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("X-Goog-Api-Key", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)) continue;
                if (all || patterns.Any(p => p.IsMatch(name))) Put(result, name, text.Trim());
            }
        foreach (var (key, node) in config)
        {
            var name = key.Trim();
            if (Rule(name)) continue;
            var template = node!.GetValue<string>();
            var trimmed = template.Trim();
            if (trimmed.StartsWith(ClientPrefix, StringComparison.Ordinal))
            {
                if (channelTest) continue;
                var source = trimmed[ClientPrefix.Length..^1].Trim();
                var value = client.FirstOrDefault(p => p.Key.Equals(source, StringComparison.OrdinalIgnoreCase)).Value;
                if (value is not null) Put(result, name, value);
            }
            else Put(result, name, template.Replace("{api_key}", apiKey, StringComparison.Ordinal));
        }
        return result;
    }

    private static void Put(Dictionary<string, string> result, string name, string value)
    {
        if (value.Length > 8192 || value.Any(char.IsControl)) throw new FormatException("请求头值包含控制字符或长度超过8192。");
        if (!string.IsNullOrWhiteSpace(value)) result[name] = value;
    }
}
