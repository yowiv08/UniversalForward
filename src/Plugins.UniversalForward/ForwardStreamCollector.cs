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
    private readonly Dictionary<int, StringBuilder> _toolInputs = [];
    private readonly HashSet<int> _closedBlocks = [];
    private bool _messageDelta;

    internal static async Task<byte[]> CollectAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, string endpoint,
        CancellationToken ct)
    {
        var collector = new ForwardStreamCollector(endpoint);
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var pending = new StringBuilder();
        var data = new StringBuilder();
        var bytesRead = 0L;
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            bytesRead += chunk.Length;
            if (bytesRead > MaxBytes) throw new IOException("上游流式响应超过 32 MiB");
            var chars = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
            pending.Append(chars, 0, decoder.GetChars(chunk.Span, chars, false));
            var text = pending.ToString();
            var start = 0;
            int newline;
            while ((newline = text.IndexOf('\n', start)) >= 0)
            {
                var line = text[start..newline].TrimEnd('\r');
                start = newline + 1;
                if (line.Length == 0)
                {
                    if (data.Length == 0) continue;
                    JsonObject? completed;
                    try { completed = collector.ReadEvent(data.ToString()); }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
                    { throw new IOException("上游事件内容无效", error); }
                    data.Clear();
                    if (completed is not null) return JsonSerializer.SerializeToUtf8Bytes(completed);
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length != 0) data.Append('\n');
                    data.Append(line[5..].TrimStart(' '));
                }
            }
            pending.Clear().Append(text.AsSpan(start));
        }
        decoder.GetChars([], new char[2], true);
        throw new IOException("上游事件流已中断，未收到完整结束事件");
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
                    case "text_delta": Append(target, delta, "text"); break;
                    case "thinking_delta": Append(target, delta, "thinking"); break;
                    case "signature_delta": Append(target, delta, "signature"); break;
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
                if (value["usage"] is JsonObject usage)
                {
                    _message["usage"] ??= new JsonObject();
                    foreach (var (name, node) in usage) _message["usage"]![name] = node?.DeepClone();
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

    private static void Append(JsonObject target, JsonObject delta, string property)
        => target[property] = (target[property]?.ToString() ?? "") + delta[property]?.ToString();
}
