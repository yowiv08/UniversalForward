using System.Text;
using System.Text.RegularExpressions;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    private static string ModelResponseDiagnostic(ForwardApiSettings settings, HttpRequestMessage request,
        HttpResponseMessage? response, string? body, string? error = null)
    {
        string Redact(string value)
        {
            var secrets = new List<string> { settings.ApiKey };
            secrets.AddRange((settings.Keys ?? []).Select(key => key.Secret));
            foreach (var header in request.Headers)
                if (Regex.IsMatch(header.Key, "authorization|cookie|token|key|secret", RegexOptions.IgnoreCase))
                    secrets.AddRange(header.Value.SelectMany(v => new[] { v, v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? v[7..] : v }));
            foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length))
                value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            value = Regex.Replace(value, @"(?im)^((?:set-cookie|cookie|authorization|proxy-authorization)\s*:)[^\r\n]*",
                "$1 [REDACTED]");
            value = Regex.Replace(value,
                """(?i)("(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|authorization|cookie|set-cookie)"\s*:\s*)"(?:\\.|[^"\\])*" """.Trim(),
                "$1\"[REDACTED]\"");
            return Regex.Replace(value, @"(?i)([?&](?:api[_-]?key|token|access_token|secret|password)=)[^&#\s]*", "$1[REDACTED]");
        }

        var content = body is null ? "（未读取到响应正文）" : body.Length == 0 ? "（响应正文为空）" : Redact(body);
        const int limit = 64 * 1024;
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > limit)
        {
            var length = limit;
            while (length > 0 && (bytes[length] & 0xc0) == 0x80) length--;
            content = Encoding.UTF8.GetString(bytes, 0, length) + "\n[响应正文已截断：最多展示 64 KiB]";
        }
        var original = BuildUri(settings.BaseUrl, "/v1/models");
        return Redact(
            $"出站方式: {settings.NetworkMode}\n请求地址: {original}\n最终地址: {response?.RequestMessage?.RequestUri?.ToString() ?? request.RequestUri?.ToString()}\n" +
            $"HTTP: {(response is null ? "未收到响应" : $"{(int)response.StatusCode} {response.ReasonPhrase}")}\n" +
            $"Content-Type: {response?.Content.Headers.ContentType?.ToString() ?? "（无）"}\n" +
            $"Content-Encoding: {(response is null || response.Content.Headers.ContentEncoding.Count == 0 ? "（无，或已由 HTTP 客户端解压）" : string.Join(", ", response.Content.Headers.ContentEncoding))}\n" +
            (error is null ? "" : $"错误: {error}\n") +
            $"响应正文:\n{content}");
    }
}
