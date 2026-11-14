using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Plugins.UniversalForward;

/// <summary>删除 Responses 历史中绑定上游资源的状态，保留消息和工具调用内容。</summary>
internal static partial class ResponsesResourceState
{
    /// <summary>仅识别上游错误中的资源状态证据，不从正文或普通参数错误推断需要剥离。</summary>
    internal static string? RecoveryReason(ForwardResponseAnalysis analysis)
    {
        if (!analysis.HasError || analysis.HasOutput || analysis.PermanentError || analysis.RateLimited)
            return null;

        var code = analysis.ErrorCode.ToLowerInvariant();
        if (code is "invalid_encrypted_content" or "encrypted_content_invalid"
            or "encrypted_content_verification_failed" or "encrypted_content_decryption_failed")
            return "invalid_encrypted_content";
        if (code is "previous_response_not_found" or "response_not_found")
            return "response_not_found";
        if (code == "item_not_found") return "item_not_found";

        var message = analysis.ErrorMessage;
        var parameter = analysis.ErrorPayload is { ValueKind: JsonValueKind.Object } error
            && error.TryGetProperty("param", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";
        var inputField = InputStateField().Match(parameter);
        var encryptedField = inputField.Success && inputField.Groups[1].Value.StartsWith("encrypted", StringComparison.Ordinal);
        if ((encryptedField || ContainsAny(message, "encrypted_content", "encrypted content", "encrypted reasoning"))
            && ContainsAny(message, "invalid", "could not be verified", "cannot be verified", "failed to verify",
                "verification failed", "could not be decrypted", "cannot be decrypted", "failed to decrypt", "decryption failed",
                "not supported", "unsupported")
            || encryptedField && ContainsAny(message, "required", "missing"))
            return "invalid_encrypted_content";

        var missing = ContainsAny(message, "not found", "does not exist", "no longer exists", "could not be found");
        if (missing && (parameter == "previous_response_id"
            || ContainsAny(message, "previous_response_id", "previous response", "response with id", "response with the id"))
            || ContainsAny(message, "no response found with id"))
            return "response_not_found";
        if (missing && (inputField.Success && inputField.Groups[1].Value == "id"
            || ContainsAny(message, "item with id", "item with the id"))
            || ContainsAny(message, "no item found with id"))
            return "item_not_found";
        return null;
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^input(?:\[[0-9]+\]|\.[0-9]+)\.(id|encrypted[^.\[\]]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex InputStateField();

    internal static int Strip(JsonObject body)
        => Strip(body, out _);

    internal static int Strip(JsonObject body, out int removedReasoningItems)
    {
        removedReasoningItems = 0;
        var input = body["input"] as JsonArray;
        // 校验整份历史后再修改，不能把检查点或引用删成缺失上下文的空壳。
        if (input is not null)
            foreach (var node in input)
                if (node is JsonObject item && ItemType(item) is "compaction" or "item_reference")
                    throw new FormatException("Responses 历史包含 compaction 加密检查点或 item_reference 条目引用，无法剥离并恢复完整历史。请提供完整消息和工具历史后重试。");

        var stripped = body.Remove("previous_response_id") ? 1 : 0;
        if (input is null) return stripped;

        for (var index = input.Count - 1; index >= 0; index--)
        {
            if (input[index] is not JsonObject item) continue;
            // 去密文后的 reasoning 仍可能被严格 Responses 上游拒绝，整条删除。
            if (ItemType(item) == "reasoning")
            {
                input.RemoveAt(index);
                removedReasoningItems++;
                continue;
            }
            if (item.Remove("id")) stripped++;
            // 仅处理直接条目字段，避免删除工具参数或消息内容中的同名字段。
            foreach (var key in item.Select(field => field.Key)
                .Where(key => key.StartsWith("encrypted", StringComparison.Ordinal)).ToArray())
            {
                item.Remove(key);
                stripped++;
            }
        }

        return stripped;
    }

    private static string? ItemType(JsonObject item)
        => item["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;
}
