using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Plugins.UniversalForward;

/// <summary>只保留用量计数；流式事件中的计数是覆盖快照，不是增量。</summary>
internal sealed class RequestTokenUsage(bool anthropic = false)
{
    private bool _anthropic = anthropic;
    private long? _input, _output, _cacheRead, _cacheWrite;
    private bool _invalidCacheRead, _invalidCacheWrite;
    private long? _fallbackInput;
    private long? _fallbackOutput;

    private long? InputTokens
    {
        get
        {
            if (_input is not { } input) return _fallbackInput;
            if (!_anthropic) return input;
            if (_invalidCacheRead || _invalidCacheWrite) return _fallbackInput;
            try { return checked(input + (_cacheRead ?? 0) + (_cacheWrite ?? 0)); }
            catch (OverflowException) { return _fallbackInput; }
        }
    }
    private long? OutputTokens => _output ?? _fallbackOutput;

    internal JsonObject ToJson() => new()
    {
        ["inputTokens"] = InputTokens, ["outputTokens"] = OutputTokens,
        ["cacheReadTokens"] = _cacheRead, ["cacheWriteTokens"] = _cacheWrite
    };

    internal bool Observe(JsonNode? reportedUsage, bool anthropic)
    {
        var before = (InputTokens, OutputTokens, _cacheRead, _cacheWrite);
        if (reportedUsage is not JsonObject usage)
        {
            _input = _output = _cacheRead = _cacheWrite = null;
            _fallbackInput = _fallbackOutput = null;
            _invalidCacheRead = _invalidCacheWrite = false;
            return before != (InputTokens, OutputTokens, _cacheRead, _cacheWrite);
        }
        var openAi = usage.ContainsKey("prompt_tokens") || usage.ContainsKey("input_tokens_details")
            || usage.ContainsKey("prompt_tokens_details");
        if (openAi) _anthropic = false;
        else if (anthropic || usage.ContainsKey("cache_read_input_tokens")) _anthropic = true;
        ReadAlias(usage, "prompt_tokens", "input_tokens", ref _input);
        ReadAlias(usage, "completion_tokens", "output_tokens", ref _output);
        if (_anthropic)
        {
            Read(usage, "cache_read_input_tokens", ref _cacheRead, ref _invalidCacheRead);
            Read(usage, "cache_creation_input_tokens", ref _cacheWrite, ref _invalidCacheWrite);
        }
        else
        {
            if (usage.TryGetPropertyValue("input_tokens_details", out var details)
                || usage.TryGetPropertyValue("prompt_tokens_details", out details))
            {
                if (details is JsonObject fields) Read(fields, "cached_tokens", ref _cacheRead, ref _invalidCacheRead);
                else { _cacheRead = null; _invalidCacheRead = true; }
            }
            Read(usage, "cache_creation_input_tokens", ref _cacheWrite, ref _invalidCacheWrite);
        }
        return before != (InputTokens, OutputTokens, _cacheRead, _cacheWrite);
    }

    internal bool FillMissing(Usage usage)
    {
        var before = (InputTokens, OutputTokens);
        if (InputTokens is null && usage.PromptTokens >= 0) _fallbackInput = usage.PromptTokens;
        if (OutputTokens is null && usage.CompletionTokens >= 0) _fallbackOutput = usage.CompletionTokens;
        return before != (InputTokens, OutputTokens);
    }

    private static void ReadAlias(JsonObject usage, string first, string second, ref long? count)
    {
        if (usage.TryGetPropertyValue(first, out var value) || usage.TryGetPropertyValue(second, out value))
            count = Count(value);
    }

    private static void Read(JsonObject usage, string name, ref long? count, ref bool invalid)
    {
        if (!usage.TryGetPropertyValue(name, out var value)) return;
        count = Count(value); invalid = count is null;
    }

    private static long? Count(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0 ? count : null;
}
