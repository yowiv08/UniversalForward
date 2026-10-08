using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Plugins.UniversalForward;
/// <summary>请求头校验与透传配置。</summary>
internal static class ForwardHeaders
{
    private static readonly System.Reflection.PropertyInfo? FullHeaders = typeof(AdapterRequest).GetProperty("DownstreamRequestHeaders");
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding",
        "Upgrade", "Content-Length", "Cookie", "Set-Cookie", "Accept-Encoding"
    };
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "X-Api-Key", "X-Goog-Api-Key", "X-Csrf-Token",
        "Content-Encoding", "Content-MD5", "Content-Digest", "Repr-Digest", "Digest"
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

    internal static Dictionary<string, string[]> ReadClientHeaders(AdapterRequest source, JsonObject extra)
    {
        var headers = source.RequestHeaders.ToDictionary(p => p.Key, p => new[] { p.Value }, StringComparer.OrdinalIgnoreCase);
        // Older hosts only expose RequestHeaders; newer hosts also retain the complete received values.
        // The full snapshot includes local credentials, which must be filtered before forwarding or template expansion.
        if (FullHeaders?.GetValue(source) is IReadOnlyDictionary<string, string?[]> full)
            foreach (var (name, values) in full)
                headers[name] = values.Where(value => value is not null).Select(value => value!).ToArray();
        var excluded = headers.Where(p => p.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value).SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keyHeader = extra["apiKeyHeader"]?.GetValue<string>();
        foreach (var name in headers.Keys.ToArray())
            if (!SafeName(name) || NotForwarded.Contains(name) || excluded.Contains(name)
                || name.Equals(keyHeader, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                headers.Remove(name);
        return headers;
    }

    internal static void ApplyClientHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string[]> headers)
    {
        foreach (var (name, values) in headers)
        {
            if (values.Sum(value => value.Length) > 8192 || values.Any(value => value.Any(char.IsControl)))
                throw new FormatException($"客户端请求头 {name} 包含非法值。");
            if (request.Headers.NonValidated.Contains(name)) request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, values) && request.Content is { } content)
            {
                content.Headers.Remove(name);
                content.Headers.TryAddWithoutValidation(name, values);
            }
        }
    }
}
