using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>将 Responses 和 Messages 事件流汇总为原生 JSON 响应。</summary>
internal sealed class ForwardStreamCollector(string endpoint)
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private JsonObject? _message;
    private readonly SortedDictionary<int, JsonObject> _blocks = [];
    private readonly Dictionary<int, Dictionary<string, StringBuilder>> _textParts = [];
    private readonly Dictionary<int, StringBuilder> _toolInputs = [];
    private readonly HashSet<int> _closedBlocks = [];
    private bool _messageDelta;
    private bool _invalidUsage;

    internal static async Task<byte[]> CollectAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, string endpoint,
        CancellationToken ct)
    {
        var collector = new ForwardStreamCollector(endpoint);
        using var reader = new SseEventReader(MaxBytes);
        byte[]? completed = null;
        Func<string, bool> onEvent = CollectEvent;
        var bytesRead = 0L;
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            bytesRead += chunk.Length;
            if (bytesRead > MaxBytes) throw new IOException("上游流式响应超过 32 MiB");
            if (reader.Observe(chunk.Span, onEvent)) return completed!;
        }
        reader.Complete();
        throw new IOException("上游事件流已中断，未收到完整结束事件");

        bool CollectEvent(string data)
        {
            JsonObject? response;
            try { response = collector.ReadEvent(data); }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
            { throw new IOException("上游事件内容无效", error); }
            if (response is null) return false;
            completed = JsonSerializer.SerializeToUtf8Bytes(response);
            return true;
        }
    }

    private JsonObject? ReadEvent(string data)
    {
        if (data == "[DONE]") throw new IOException("上游事件流缺少协议结束事件");
        JsonObject value;
        try { value = JsonNode.Parse(data) as JsonObject ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("上游事件不是有效的 JSON 对象", error); }
        var type = value["type"]?.ToString();
        if (type is "error" or "response.failed" || value["error"] is not null)
            throw new IOException(value["error"]?["message"]?.ToString()
                ?? value["response"]?["error"]?["message"]?.ToString() ?? "上游事件流返回错误");
        if (endpoint == "/v1/responses")
        {
            if (type is not ("response.completed" or "response.incomplete")) return null;
            if (value["response"] is not JsonObject response
                || response["output"] is not JsonArray || response["status"]?.ToString() is not ("completed" or "incomplete"))
                throw new IOException("上游结束事件缺少完整 Responses 响应");
            return response;
        }
        switch (type)
        {
            case "message_start":
                if (_message is not null || value["message"] is not JsonObject message
                    || message["content"] is not JsonArray)
                    throw new IOException("上游 message_start 内容无效");
                _message = (JsonObject)message.DeepClone();
                _invalidUsage = message.TryGetPropertyValue("usage", out var startUsage) && startUsage is not JsonObject;
                break;
            case "content_block_start":
                var index = Index(value);
                if (_message is null || _blocks.ContainsKey(index) || value["content_block"] is not JsonObject block)
                    throw new IOException("上游内容块起始事件无效");
                _blocks[index] = (JsonObject)block.DeepClone();
                break;
            case "content_block_delta":
                var deltaIndex = Index(value);
                if (!_blocks.TryGetValue(deltaIndex, out var target) || _closedBlocks.Contains(deltaIndex)
                    || value["delta"] is not JsonObject delta)
                    throw new IOException("上游内容增量缺少对应内容块");
                switch (delta["type"]?.ToString())
                {
                    case "text_delta": Append(deltaIndex, target, delta, "text"); break;
                    case "thinking_delta": Append(deltaIndex, target, delta, "thinking"); break;
                    case "signature_delta": Append(deltaIndex, target, delta, "signature"); break;
                    case "input_json_delta":
                        if (!_toolInputs.TryGetValue(deltaIndex, out var json)) _toolInputs[deltaIndex] = json = new StringBuilder();
                        json.Append(delta["partial_json"]?.ToString());
                        break;
                    case "citations_delta":
                        if (target["citations"] is null) target["citations"] = new JsonArray();
                        target["citations"]!.AsArray().Add(delta["citation"]?.DeepClone());
                        break;
                    default: throw new IOException("上游返回不支持的内容增量：" + delta["type"]?.ToString());
                }
                break;
            case "content_block_stop":
                var stopIndex = Index(value);
                if (!_blocks.TryGetValue(stopIndex, out var stoppedBlock) || !_closedBlocks.Add(stopIndex))
                    throw new IOException("上游内容块结束事件无效");
                if (_textParts.Remove(stopIndex, out var parts))
                    foreach (var (property, text) in parts) stoppedBlock[property] = text.ToString();
                if (_toolInputs.TryGetValue(stopIndex, out var input))
                {
                    try { stoppedBlock["input"] = JsonNode.Parse(input.ToString()) as JsonObject ?? throw new JsonException(); }
                    catch (JsonException error) { throw new IOException("上游工具参数不是有效 JSON 对象", error); }
                }
                break;
            case "message_delta":
                if (_message is null || value["delta"] is not JsonObject messageDelta)
                    throw new IOException("上游 message_delta 内容无效");
                foreach (var (name, node) in messageDelta) _message[name] = node?.DeepClone();
                if (!_invalidUsage && value.TryGetPropertyValue("usage", out var reportedUsage))
                {
                    // 用量格式错误不影响正文汇总；保留无效值，让宿主从原文解析或估算。
                    if (reportedUsage is not JsonObject usage)
                    {
                        _message["usage"] = reportedUsage?.DeepClone();
                        _invalidUsage = true;
                    }
                    else if (_message["usage"] is null or JsonObject)
                    {
                        _message["usage"] ??= new JsonObject();
                        foreach (var (name, node) in usage) _message["usage"]![name] = node?.DeepClone();
                    }
                }
                _messageDelta = true;
                break;
            case "message_stop":
                if (_message is null || !_messageDelta || _closedBlocks.Count != _blocks.Count
                    || _message["stop_reason"] is null)
                    throw new IOException("上游 Messages 响应不完整");
                if (_blocks.Count != 0) _message["content"] = new JsonArray(_blocks.Values.Select(x => (JsonNode)x.DeepClone()).ToArray());
                return _message;
        }
        return null;
    }

    private static int Index(JsonObject value)
        => value["index"] is JsonValue index && index.TryGetValue<int>(out var number) && number is >= 0 and < 10000
            ? number : throw new IOException("上游内容块索引无效");

    private void Append(int index, JsonObject target, JsonObject delta, string property)
    {
        if (!_textParts.TryGetValue(index, out var parts))
            _textParts[index] = parts = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        if (!parts.TryGetValue(property, out var text))
            parts[property] = text = new StringBuilder(target[property]?.ToString() ?? "");
        text.Append(delta[property]?.ToString());
    }
}
