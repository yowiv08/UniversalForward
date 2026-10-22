using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>删除 Responses 历史中绑定上游资源的状态，保留消息和工具调用内容。</summary>
internal static class ResponsesResourceState
{
    internal static int Strip(JsonObject body)
    {
        var stripped = body.Remove("previous_response_id") ? 1 : 0;
        if (body["input"] is not JsonArray input) return stripped;

        foreach (var node in input)
        {
            if (node is not JsonObject item) continue;
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
}
