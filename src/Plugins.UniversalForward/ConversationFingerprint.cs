using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>规范化请求中的聊天历史，仅返回逐项前缀摘要。</summary>
internal sealed record ConversationFingerprint(IReadOnlyList<string> Prefixes, bool HasReply)
{
    internal static ConversationFingerprint Read(JsonNode? body, string endpoint)
    {
        if (body is not JsonObject root) return new([], false);
        var input = endpoint == "/v1/messages" ? root["messages"] ?? root["input"] : root["input"] ?? root["messages"];
        IEnumerable<JsonNode?> items = input switch
        {
            JsonArray array => array,
            JsonValue value when value.TryGetValue<string>(out _) => new[] { (JsonNode?)value },
            _ => Array.Empty<JsonNode?>()
        };
        var prefixes = new List<string>();
        var hasReply = false;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new HashStream(digest);
        foreach (var item in items)
        {
            if (item is null) return new([], false);
            var role = Text((item as JsonObject)?["role"]);
            // System/developer instructions are request settings, not a chat boundary.
            if (role is "system" or "developer") continue;
            using (var writer = new Utf8JsonWriter(sink))
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text))
                    WriteMessage(writer, "user", JsonValue.Create(text), null);
                else if (item is JsonObject message && role is not null)
                {
                    hasReply |= role is "assistant" or "tool" || ContainsToolResult(message["content"]);
                    WriteMessage(writer, role, message["content"], message);
                }
                else
                {
                    var type = Text((item as JsonObject)?["type"]);
                    hasReply |= type is "function_call" or "function_call_output" or "tool_use" or "tool_result" or "reasoning";
                    WriteBlock(writer, item);
                }
            }
            digest.AppendData("\n"u8);
            prefixes.Add(Convert.ToHexString(digest.GetCurrentHash()));
        }
        return new(prefixes, hasReply);
    }

    private static string? Text(JsonNode? value) => value is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    private static bool ContainsToolResult(JsonNode? content) => content is JsonArray array
        && array.OfType<JsonObject>().Any(block => Text(block["type"]) is "tool_result" or "function_call_output");

    private static void WriteMessage(Utf8JsonWriter writer, string role, JsonNode? content, JsonObject? message)
    {
        writer.WriteStartArray();
        writer.WriteStringValue("message");
        writer.WriteStringValue(role);
        WriteContent(writer, content);
        // Retain tool linkage and named speakers; discard transport message IDs/status.
        foreach (var name in new[] { "name", "tool_call_id", "tool_calls", "function_call", "refusal" })
            if (message?[name] is { } value)
            {
                writer.WriteStringValue(name);
                WriteCanonical(writer, value);
            }
        writer.WriteEndArray();
    }

    private static void WriteContent(Utf8JsonWriter writer, JsonNode? content)
    {
        writer.WriteStartArray();
        if (Text(content) is { } text) WriteText(writer, text);
        else if (content is JsonArray blocks)
        {
            StringBuilder? adjacentText = null;
            foreach (var block in blocks)
            {
                if (Text(block) is { } blockText) (adjacentText ??= new()).Append(blockText);
                else if (block is JsonObject item && (Text(item["type"]) is "text" or "input_text" or "output_text")
                    && Text(item["text"]) is { } typedText)
                    (adjacentText ??= new()).Append(typedText);
                else
                {
                    if (adjacentText is not null) { WriteText(writer, adjacentText.ToString()); adjacentText = null; }
                    WriteBlock(writer, block);
                }
            }
            if (adjacentText is not null) WriteText(writer, adjacentText.ToString());
        }
        else if (content is not null) WriteCanonical(writer, content);
        writer.WriteEndArray();
    }

    private static void WriteText(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartArray();
        writer.WriteStringValue("text");
        writer.WriteStringValue(text);
        writer.WriteEndArray();
    }

    private static void WriteBlock(Utf8JsonWriter writer, JsonNode? block)
    {
        if (block is not JsonObject item) { WriteCanonical(writer, block); return; }
        var type = Text(item["type"]);
        if (type is "function_call" or "tool_use")
        {
            writer.WriteStartArray();
            writer.WriteStringValue("tool_call");
            writer.WriteStringValue(Text(item["call_id"]) ?? Text(item["id"]));
            writer.WriteStringValue(Text(item["name"]));
            WriteArguments(writer, item["arguments"] ?? item["input"]);
            writer.WriteEndArray();
        }
        else if (type is "function_call_output" or "tool_result")
        {
            writer.WriteStartArray();
            writer.WriteStringValue("tool_result");
            writer.WriteStringValue(Text(item["call_id"]) ?? Text(item["tool_use_id"]));
            WriteContent(writer, item["output"] ?? item["content"]);
            WriteCanonical(writer, item["is_error"]);
            writer.WriteEndArray();
        }
        else WriteCanonical(writer, block);
    }

    private static void WriteArguments(Utf8JsonWriter writer, JsonNode? arguments)
    {
        if (Text(arguments) is { } text)
        {
            try { WriteCanonical(writer, JsonNode.Parse(text)); return; }
            catch (JsonException) { }
        }
        WriteCanonical(writer, arguments);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    // Stream canonical JSON into the hash, without retaining a serialized body copy.
    private sealed class HashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
