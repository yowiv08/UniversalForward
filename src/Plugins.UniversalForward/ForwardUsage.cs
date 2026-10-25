using System.Text.Json;
using Router.Contracts.Domain;

namespace Plugins.UniversalForward;

internal static class ForwardUsage
{
    // 不完整的 Usage 留给宿主从原文解析/估算，不能用伪造的零值抢占宿主统计。
    internal static Usage? Read(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object) return null;
            if (!Count(usage, "prompt_tokens", out var prompt)
                || !Count(usage, "input_tokens", out var input)
                || !Count(usage, "completion_tokens", out var completion)
                || !Count(usage, "output_tokens", out var output)
                || !Count(usage, "total_tokens", out var total)) return null;
            var inputCount = prompt ?? input;
            var outputCount = completion ?? output;
            if (inputCount is null || outputCount is null) return null;
            if (prompt is null)
            {
                if (!Count(usage, "cache_read_input_tokens", out var cacheRead)
                    || !Count(usage, "cache_creation_input_tokens", out var cacheWrite)) return null;
                inputCount = checked(inputCount.Value + (cacheRead ?? 0) + (cacheWrite ?? 0));
            }
            return new Usage(inputCount.Value, outputCount.Value, total ?? checked(inputCount.Value + outputCount.Value));
        }
        catch (Exception error) when (error is JsonException or OverflowException) { return null; }
    }

    private static bool Count(JsonElement usage, string name, out int? count)
    {
        count = null;
        if (!usage.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0) return false;
        count = number;
        return true;
    }
}
