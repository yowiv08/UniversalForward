using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>删除 Responses 历史中绑定上游资源的状态，保留消息和工具调用内容。</summary>
internal static class ResponsesResourceState
{
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
                    throw new FormatException("Responses 历史包含 compaction 加密检查点或 item_reference 条目引用，无法跨资源恢复完整历史。请提供完整消息和工具历史后再开启资源状态剥离。");

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
