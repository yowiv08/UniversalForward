using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>渠道的系统提示词改写规则。</summary>
public sealed class SystemPromptPolicy
{
    public string Mode { get; set; } = "off";
    public string Text { get; set; } = string.Empty;

    internal void Validate()
    {
        if (Mode is not ("off" or "append" or "replace"))
            throw new FormatException("系统提示词处理方式必须为不改写、追加或替换。");
        if (Text is null)
            throw new FormatException("系统提示词必须为文本。");
        if (Mode != "off" && string.IsNullOrWhiteSpace(Text))
            throw new FormatException("追加或替换时，系统提示词不能为空。");
    }

    internal SystemPromptRewriteDecision? Apply(JsonObject body, string endpoint, bool preserveClaudeIdentity = false)
    {
        Validate();
        if (Mode == "off") return null;
        return endpoint switch
        {
            "/v1/responses" => ApplyResponses(body),
            "/v1/messages" => ApplyMessages(body, preserveClaudeIdentity),
            "/v1/chat/completions" => ApplyChat(body),
            _ => null
        };
    }

    private SystemPromptRewriteDecision? ApplyResponses(JsonObject body)
    {
        var input = ReadInputMessages(body["input"]);
        var removed = 0;
        if (Mode == "append")
        {
            var original = ReadInstructions(body["instructions"]);
            if (ContainsPrompt(original) || SystemMessagesContainPrompt(input, "input_text")) return null;
            body["instructions"] = AppendText(original);
        }
        else
        {
            if (input is not null) removed = RemoveSystemMessages(input);
            if (StringValue(body["instructions"]) != Text) body["instructions"] = Text;
            else if (removed == 0) return null;
        }
        return new(Mode, "instructions", Text.Length, removed, Placement: "top_level", PriorityRole: "system_or_developer");
    }

    private SystemPromptRewriteDecision? ApplyMessages(JsonObject body, bool preserveClaudeIdentity)
    {
        var original = body["system"];
        if (Mode == "append")
        {
            if (ContentContainsPrompt(original, "text")) return null;
            var blocks = ReadSystem(original);
            blocks.Add(TextBlock(Text));
            if (!ReferenceEquals(original, blocks)) body["system"] = blocks;
        }
        else
        {
            var replacement = new JsonArray(TextBlock(Text));
            if (preserveClaudeIdentity) ClientProfiles.EnsureClaudeSystemIdentity(replacement);
            if (SameSystemText(original, replacement)) return null;
            if (original is JsonArray current)
            {
                for (var i = 0; i < replacement.Count; i++)
                {
                    var wanted = replacement[i]!["text"]!.GetValue<string>();
                    if (FindTextBlock(current, wanted) is not { } existing) continue;
                    current.Remove(existing);
                    replacement[i] = existing;
                }
            }
            body["system"] = replacement;
        }
        return new(Mode, "system", Text.Length, 0, TemplateIdentityPreserved: Mode == "replace" && preserveClaudeIdentity);
    }

    private SystemPromptRewriteDecision? ApplyChat(JsonObject body)
    {
        var messages = body["messages"] switch
        {
            null => new JsonArray(),
            JsonArray array => array,
            _ => throw new FormatException("改写系统提示词时，Chat Completions messages 必须为数组或 null。")
        };
        var leading = messages.Count > 0 && messages[0] is JsonObject first && IsSystemMessage(first) ? first : null;
        var role = StringValue(leading?["role"]) ?? "system";
        var removed = 0;
        if (Mode == "append")
        {
            if (SystemMessagesContainPrompt(messages, "text")) return null;
            if (leading?["content"] is JsonArray parts) parts.Add(TextBlock(Text));
            else if (StringValue(leading?["content"]) is { } original) leading!["content"] = AppendText(original);
            else messages.Insert(0, new JsonObject { ["role"] = role, ["content"] = Text });
        }
        else
        {
            var (keep, block) = FindChatPrompt(messages);
            removed = RemoveSystemMessages(messages, keep);
            if (keep is null) messages.Insert(0, new JsonObject { ["role"] = role, ["content"] = Text });
            else
            {
                if (block is not null && keep["content"] is JsonArray { Count: > 1 } parts)
                {
                    parts.Clear();
                    parts.Add(block);
                }
                else if (removed == 0 && ReferenceEquals(keep, leading)) return null;
                if (!ReferenceEquals(keep, messages[0]))
                {
                    messages.Remove(keep);
                    messages.Insert(0, keep);
                }
                if (StringValue(keep["role"]) != role) keep["role"] = role;
            }
        }
        if (!ReferenceEquals(body["messages"], messages)) body["messages"] = messages;
        return new(Mode, "messages", Text.Length, removed, Placement: "first", PriorityRole: role);
    }

    private (JsonObject? Message, JsonObject? Block) FindChatPrompt(JsonArray messages)
    {
        foreach (var node in messages)
        {
            if (node is not JsonObject item || !IsSystemMessage(item)) continue;
            var content = item["content"];
            if (StringValue(content) == Text) return (item, null);
            if (content is JsonArray parts && FindTextBlock(parts, Text) is { } block) return (item, block);
        }
        return (null, null);
    }

    private static JsonObject? FindTextBlock(JsonArray parts, string text)
    {
        foreach (var part in parts)
            if (part is JsonObject block && BlockText(block, "text") == text) return block;
        return null;
    }

    private bool SystemMessagesContainPrompt(JsonArray? messages, string blockType)
    {
        if (messages is not null)
            foreach (var node in messages)
                if (node is JsonObject item && IsSystemMessage(item) && ContentContainsPrompt(item["content"], blockType)) return true;
        return false;
    }

    private bool ContentContainsPrompt(JsonNode? content, string blockType)
    {
        if (content is not JsonArray parts) return ContainsPrompt(StringValue(content));
        foreach (var part in parts)
            if (ContainsPrompt(BlockText(part, blockType))) return true;
        return false;
    }

    private bool ContainsPrompt(string? content)
    {
        if (content is null || content.Length < Text.Length) return false;
        var from = 0;
        while (from <= content.Length - Text.Length)
        {
            var start = content.IndexOf(Text, from, StringComparison.Ordinal);
            if (start < 0) break;
            var end = start + Text.Length;
            if ((start == 0 || IsLineBreak(content[start - 1]) || IsLineBreak(Text[0]))
                && (end == content.Length || IsLineBreak(content[end]) || IsLineBreak(Text[^1]))) return true;
            // 同一行内的其他子串也不可能是独立指令，从下一个换行符继续。
            var nextBreak = content.AsSpan(start + 1).IndexOfAny('\r', '\n');
            if (nextBreak < 0) break;
            from = start + 1 + nextBreak;
        }
        return false;
    }

    private static bool SameSystemText(JsonNode? original, JsonArray replacement)
    {
        if (original is not JsonArray blocks)
            return replacement.Count == 1 && StringValue(original) == BlockText(replacement[0], "text");
        if (blocks.Count != replacement.Count) return false;
        for (var i = 0; i < blocks.Count; i++)
            if (BlockText(blocks[i], "text") != BlockText(replacement[i], "text")) return false;
        return true;
    }

    private static string? StringValue(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool IsLineBreak(char c) => c is '\r' or '\n';
    private static string? BlockText(JsonNode? node, string type) => node is JsonObject block && StringValue(block["type"]) == type ? StringValue(block["text"]) : null;
    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
    private string AppendText(string? original) => string.IsNullOrEmpty(original) ? Text : original + "\n\n" + Text;
    private static JsonArray? ReadInputMessages(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => array,
        JsonValue value when value.TryGetValue<string>(out _) => null,
        _ => throw new FormatException("改写系统提示词时，Responses input 必须为字符串、数组或 null。")
    };
    private static string? ReadInstructions(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new FormatException("追加系统提示词时，Responses instructions 必须为字符串或 null。")
    };
    private static JsonArray ReadSystem(JsonNode? node) => node switch
    {
        null => new JsonArray(),
        JsonArray array => array,
        JsonValue value when value.TryGetValue<string>(out var text) => string.IsNullOrEmpty(text)
            ? new JsonArray() : new JsonArray(TextBlock(text)),
        _ => throw new FormatException("追加系统提示词时，Messages system 必须为字符串、数组或 null。")
    };
    private static int RemoveSystemMessages(JsonArray messages, JsonObject? keep = null)
    {
        var originalCount = messages.Count;
        for (var index = messages.Count - 1; index >= 0; index--)
            if (messages[index] is JsonObject item && !ReferenceEquals(item, keep) && IsSystemMessage(item)) messages.RemoveAt(index);
        return originalCount - messages.Count;
    }
    private static bool IsSystemMessage(JsonObject item)
    {
        if (StringValue(item["role"]) is not ("system" or "developer")) return false;
        return (!item.ContainsKey("type") || StringValue(item["type"]) == "message")
            && item["tools"] is not (JsonArray { Count: > 0 } or JsonObject { Count: > 0 });
    }
}

internal sealed record SystemPromptRewriteDecision(string Mode, string Field, int TextLength, int RemovedMessages,
    bool TemplateIdentityPreserved = false, int DemotedMessages = 0, string PriorityRole = "system", string Placement = "last");
