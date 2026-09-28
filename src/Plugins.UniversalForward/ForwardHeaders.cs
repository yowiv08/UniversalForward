using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;
/// <summary>请求头校验与透传配置。</summary>
internal static class ForwardHeaders
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding",
        "Upgrade", "Content-Length", "Cookie", "Set-Cookie", "Accept-Encoding"
    };

    internal static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name)
        && name.Length <= 256
        && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))
        && !name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
        && !Forbidden.Contains(name);

    internal static void Validate(JsonObject extra)
    {
        foreach (var key in new[] { "apiKeyHeader", "apiKeyPrefix" })
        {
            if (!extra.TryGetPropertyValue(key, out var node)) continue;
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text)
                || text.Length > 8192 || text.Any(char.IsControl))
                throw new FormatException($"{key} 必须是不含控制字符的字符串。");
            if (key == "apiKeyHeader" && (!SafeName(text) || text.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)))
                throw new FormatException("apiKeyHeader 必须是有效的认证请求头。");
        }
        if (extra.TryGetPropertyValue("PassThroughHeaders", out var passthrough))
        {
            if (passthrough is not JsonArray names || names.Count > 64)
                throw new FormatException("PassThroughHeaders 必须是最多64项的头名数组。");
            foreach (var item in names)
                if (item is not JsonValue value || !value.TryGetValue<string>(out var name)
                    || !SafeName(name) || name == "*"
                    || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(extra["apiKeyHeader"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("PassThroughHeaders 不允许认证头、连接头或通配符。");
        }
    }

    internal static void ApplySelected(HttpRequestMessage request,
        IReadOnlyDictionary<string, string> source, JsonObject extra)
    {
        if (extra["PassThroughHeaders"] is not JsonArray names) return;
        var excluded = source.Where(p => p.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in names)
        {
            var name = item!.GetValue<string>();
            if (excluded.Contains(name)) continue;
            var matching = source.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length > 1) throw new FormatException($"客户端请求头 {name} 重复。");
            if (matching.Length == 0) continue;
            var value = matching[0].Value;
            if (value.Length > 8192 || value.Any(char.IsControl))
                throw new FormatException($"客户端请求头 {name} 包含非法值。");
            request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is { } content)
            {
                content.Headers.Remove(name);
                content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}
