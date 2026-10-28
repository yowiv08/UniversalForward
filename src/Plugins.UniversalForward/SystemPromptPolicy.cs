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
            var removed = 0;
            if (Mode == "append") body["instructions"] = Append(ReadInstructions(body["instructions"]));
            else
            {
                body["instructions"] = Text;
                if (body["input"] is JsonArray input)
                    for (var index = input.Count - 1; index >= 0; index--)
                        if (input[index] is JsonObject item && IsSystemMessage(item))
                        { input.RemoveAt(index); removed++; }
            }
            return new(Mode, "instructions", Text.Length, removed);
        }
        if (endpoint == "/v1/messages")
        {
            if (Mode == "replace") body["system"] = Text;
            else if (body["system"] is JsonArray blocks)
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = Text });
            else body["system"] = Append(ReadSystem(body["system"]));
            return new(Mode, "system", Text.Length, 0);
        }
        return null;
    }

    private string Append(string? original) => string.IsNullOrEmpty(original) ? Text : original + "\n\n" + Text;
    private static string? ReadInstructions(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new FormatException("追加系统提示词时，Responses instructions 必须为字符串或 null。")
    };
    private static string? ReadSystem(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
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
    bool TemplateIdentityPreserved = false);
