using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Plugins.UniversalForward;

/// <summary>只保留用量计数；流式事件中的计数是覆盖快照，不是增量。</summary>
internal sealed class RequestTokenUsage(bool anthropic = false)
{
    private static readonly string[] OpenAiDetailNames = ["input_tokens_details", "prompt_tokens_details"];
    private bool _anthropic = anthropic;
    private long? _input, _output, _cacheRead;
    private bool _invalidCacheRead;
    private readonly CacheWriteCounters _cacheWrite = new();
    private long? _fallbackInput;
    private long? _fallbackOutput;

    private long? InputTokens
    {
        get
        {
            if (_input is not { } input) return _fallbackInput;
            if (!_anthropic) return input;
            if (_invalidCacheRead || _cacheWrite.Invalid) return _fallbackInput;
            try { return checked(input + (_cacheRead ?? 0) + (_cacheWrite.Value ?? 0)); }
            catch (OverflowException) { return _fallbackInput; }
        }
    }
    private long? OutputTokens => _output ?? _fallbackOutput;

    internal JsonObject ToJson() => new()
    {
        ["inputTokens"] = InputTokens, ["outputTokens"] = OutputTokens,
        ["cacheReadTokens"] = _cacheRead, ["cacheWriteTokens"] = _cacheWrite.Value
    };

    internal bool Observe(JsonNode? reportedUsage, bool anthropic)
    {
        var before = (InputTokens, OutputTokens, _cacheRead, _cacheWrite.Value);
        if (reportedUsage is not JsonObject usage)
        {
            _input = _output = _cacheRead = null;
            _cacheWrite.Reset();
            _fallbackInput = _fallbackOutput = null;
            _invalidCacheRead = false;
            return before != (InputTokens, OutputTokens, _cacheRead, _cacheWrite.Value);
        }
        var openAi = usage.ContainsKey("prompt_tokens") || usage.ContainsKey("input_tokens_details")
            || usage.ContainsKey("prompt_tokens_details");
        if (openAi) _anthropic = false;
        else if (anthropic || usage.ContainsKey("cache_read_input_tokens") || usage.ContainsKey("cache_creation"))
            _anthropic = true;
        ReadAlias(usage, "prompt_tokens", "input_tokens", ref _input);
        ReadAlias(usage, "completion_tokens", "output_tokens", ref _output);
        if (_anthropic)
        {
            Read(usage, "cache_read_input_tokens", ref _cacheRead, ref _invalidCacheRead);
        }
        else
        {
            if (usage.TryGetPropertyValue("input_tokens_details", out var details)
                || usage.TryGetPropertyValue("prompt_tokens_details", out details))
            {
                if (details is JsonObject fields) Read(fields, "cached_tokens", ref _cacheRead, ref _invalidCacheRead);
                else { _cacheRead = null; _invalidCacheRead = true; }
            }
        }
        ObserveCacheWrite(usage);
        return before != (InputTokens, OutputTokens, _cacheRead, _cacheWrite.Value);
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

    private void ObserveCacheWrite(JsonObject usage)
    {
        var total = new CountSnapshot();
        total.Read(usage, "cache_creation_input_tokens");
        if (_anthropic)
        {
            if (usage.TryGetPropertyValue("cache_creation", out var split))
                _cacheWrite.ObserveSplit(split, true);
        }
        else
        {
            var first = new CountSnapshot();
            var second = new CountSnapshot();
            var invalidSplit = false;
            foreach (var name in OpenAiDetailNames)
            {
                if (!usage.TryGetPropertyValue(name, out var details)) continue;
                if (details is not JsonObject fields)
                {
                    total.Invalidate();
                    invalidSplit = true;
                    continue;
                }
                total.Read(fields, "cache_write_tokens");
                total.Read(fields, "cached_creation_tokens");
                total.Read(fields, "cache_creation_input_tokens");
                if (!fields.TryGetPropertyValue("cache_write_tokens_details", out var split)) continue;
                if (split is not JsonObject values) { invalidSplit = true; continue; }
                first.Read(values, "ttl_30m_tokens");
                second.Read(values, "ttl_12h_tokens");
            }
            _cacheWrite.ObserveSplit(first, second, invalidSplit, false);
        }
        _cacheWrite.ObserveTotal(total);
    }

    // Alias counters describe the same cumulative total. Only aliases present
    // in this snapshot participate, so a later zero can replace an earlier alias.
    private struct CountSnapshot
    {
        internal bool Present;
        internal bool Invalid;
        internal long Value;

        internal void Read(JsonObject fields, string name)
        {
            if (!fields.TryGetPropertyValue(name, out var node)) return;
            Present = true;
            if (Count(node) is { } count) Value = Math.Max(Value, count);
            else Invalid = true;
        }

        internal void Invalidate() { Present = true; Invalid = true; }
        internal readonly long? Result => Invalid ? null : Value;
    }

    private sealed class CacheWriteCounters
    {
        private bool _hasTotal, _hasFirst, _hasSecond, _invalidSplit;
        private bool? _anthropicSplit;
        private long? _total, _first, _second;

        internal long? Value
        {
            get
            {
                if (_hasTotal) return _total;
                if (_invalidSplit || (_hasFirst && _first is null) || (_hasSecond && _second is null)) return null;
                if (!_hasFirst && !_hasSecond) return null;
                try { return checked((_first ?? 0) + (_second ?? 0)); }
                catch (OverflowException) { return null; }
            }
        }

        internal bool Invalid => Value is null && (_hasTotal || _hasFirst || _hasSecond || _invalidSplit);

        internal void ObserveTotal(CountSnapshot total)
        {
            if (!total.Present) return;
            _hasTotal = true;
            _total = total.Result;
        }

        internal void ObserveSplit(JsonNode? node, bool anthropic)
        {
            var first = new CountSnapshot();
            var second = new CountSnapshot();
            if (node is JsonObject fields)
            {
                first.Read(fields, "ephemeral_5m_input_tokens");
                second.Read(fields, "ephemeral_1h_input_tokens");
            }
            ObserveSplit(first, second, node is not JsonObject, anthropic);
        }

        internal void ObserveSplit(CountSnapshot first, CountSnapshot second, bool invalid, bool anthropic)
        {
            if (!first.Present && !second.Present && !invalid) return;
            if (_anthropicSplit != anthropic || invalid)
            {
                _hasFirst = _hasSecond = false;
                _first = _second = null;
                _anthropicSplit = anthropic;
            }
            _invalidSplit = invalid;
            if (invalid) return;
            if (first.Present) { _hasFirst = true; _first = first.Result; }
            if (second.Present) { _hasSecond = true; _second = second.Result; }
        }

        internal void Reset()
        {
            _hasTotal = _hasFirst = _hasSecond = _invalidSplit = false;
            _total = _first = _second = null;
            _anthropicSplit = null;
        }
    }
}
