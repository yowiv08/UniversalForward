using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>渠道的系统提示词改写规则。</summary>
public sealed class SystemPromptPolicy
{
    internal const int MaxTextLength = 32000;
    public string Mode { get; set; } = "off";
    public string Text { get; set; } = string.Empty;

    internal void Validate()
    {
        if (Mode is not ("off" or "append" or "replace"))
            throw new FormatException("系统提示词处理方式必须为不改写、追加或替换。");
        if (Text is null || Text.Length > MaxTextLength)
            throw new FormatException("系统提示词必须为文本，长度不能超过 32000。");
        if (Mode != "off" && string.IsNullOrWhiteSpace(Text))
            throw new FormatException("追加或替换时，系统提示词不能为空。");
    }

    internal SystemPromptRewriteDecision? Apply(JsonObject body, string endpoint)
    {
        Validate();
        if (Mode == "off") return null;
        if (endpoint == "/v1/responses")
        {
            // Validate both fields before changing the request. A trailing system message has
            // its own role; concatenating channel text into instructions leaves competing roles.
            var original = Mode == "append" ? ReadInstructions(body["instructions"]) : null;
            var input = ReadInput(body["input"]);
            var removed = 0;
            var demoted = 0;
            for (var index = input.Count - 1; index >= 0; index--)
            {
                if (input[index] is not JsonObject item || !IsSystemMessage(item)) continue;
                if (Mode == "replace") { input.RemoveAt(index); removed++; }
                else if (item["role"]!.GetValue<string>() == "system")
                {
                    item["role"] = "developer";
                    demoted++;
                }
            }
            if (!string.IsNullOrEmpty(original)) input.Insert(0, Message("developer", original));
            input.Add(Message("system", Text));
            if (!ReferenceEquals(body["input"], input)) body["input"] = input;
            // Codex-compatible upstreams require this field to exist even when instructions
            // are supplied as explicit input messages. Do not send the channel text twice.
            body["instructions"] = string.Empty;
            return new(Mode, "input", Text.Length, removed, DemotedMessages: demoted);
        }
        if (endpoint == "/v1/messages")
        {
            var blocks = Mode == "replace" ? new JsonArray() : ReadSystem(body["system"]);
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = Text });
            if (!ReferenceEquals(body["system"], blocks)) body["system"] = blocks;
            return new(Mode, "system", Text.Length, 0);
        }
        return null;
    }

    private static JsonObject Message(string role, string text) => new()
    {
        ["type"] = "message", ["role"] = role,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text })
    };
    private static JsonArray ReadInput(JsonNode? node) => node switch
    {
        null => new JsonArray(),
        JsonArray array => array,
        JsonValue value when value.TryGetValue<string>(out var text) => new JsonArray(Message("user", text)),
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
            ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        _ => throw new FormatException("追加系统提示词时，Messages system 必须为字符串、数组或 null。")
    };
    private static bool IsSystemMessage(JsonObject item)
    {
        var type = item["type"];
        var message = !item.ContainsKey("type") || type is JsonValue value && value.TryGetValue<string>(out var name) && name == "message";
        var dynamicTools = item["tools"] is JsonArray { Count: > 0 } or JsonObject { Count: > 0 };
        return message && !dynamicTools && item["role"] is JsonValue role && role.TryGetValue<string>(out var text) && text is "system" or "developer";
    }
}

internal sealed record SystemPromptRewriteDecision(string Mode, string Field, int TextLength, int RemovedMessages,
    bool TemplateIdentityPreserved = false, int DemotedMessages = 0, string PriorityRole = "system", string Placement = "last");
