using System.Text.Json;
using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

internal static class ConnectionTestAssertions
{
    // 旧协议断言改为实际读取摘要和独立正文，避免把原文重新塞进 Job。
    internal static JsonElement Rows(UniversalForwardTerminal terminal, string job = "job")
    {
        var rows = new JsonArray();
        var offset = 0;
        while (true)
        {
            var page = JsonSerializer.SerializeToElement(terminal.TestResults.Page(job, offset, 20));
            foreach (var item in page.GetProperty("rows").EnumerateArray())
            {
                var row = JsonNode.Parse(item.GetRawText())!.AsObject();
                var body = new System.Text.StringBuilder();
                var bodyOffset = 0;
                while (true)
                {
                    var detail = JsonSerializer.SerializeToElement(terminal.TestResults.Detail(job, row["rowId"]!.GetValue<int>(), bodyOffset));
                    body.Append(detail.GetProperty("text").GetString());
                    if (detail.GetProperty("done").GetBoolean()) break;
                    bodyOffset = detail.GetProperty("nextOffset").GetInt32();
                }
                row["response"] = body.ToString(); rows.Add(row);
            }
            offset = page.GetProperty("nextOffset").GetInt32();
            if (offset >= page.GetProperty("total").GetInt32()) break;
        }
        return JsonSerializer.SerializeToElement(rows);
    }
}
