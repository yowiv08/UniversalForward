using System.Text;
using System.Text.Json;

namespace Plugins.UniversalForward;

internal sealed class ForwardResponseAnalysis
{
    internal bool HasOutput { get; private set; }
    internal bool HasUsage { get; private set; }
    internal bool HasError { get; private set; }
    internal bool RateLimited { get; private set; }
    internal bool PermanentError { get; private set; }
    internal bool Terminal { get; private set; }
    internal string State { get; private set; } = "missing_terminal";
    internal string IncompleteReason { get; private set; } = "";

    internal static ForwardResponseAnalysis Read(byte[] bytes, bool sse, bool interrupted)
    {
        var result = new ForwardResponseAnalysis();
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(text)) return result;
        if (!sse || text.TrimStart().StartsWith('{'))
        {
            result.ReadEvent(text);
            if (!sse) result.Terminal = true;
            return result;
        }
        var data = new List<string>();
        var eventType = "";
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length == 0)
            {
                if (data.Count > 0) result.ReadEvent(string.Join("\n", data), eventType);
                data.Clear();
                eventType = "";
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Add(line[5..].TrimStart(' '));
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].Trim();
        }
        if (data.Count > 0)
        {
            try { result.ReadEvent(string.Join("\n", data), eventType); }
            catch (IOException) when (interrupted) { result.HasOutput = true; }
        }
        return result;
    }

    private static string Text(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : "";

    internal void ReadEvent(string text, string eventType = "")
    {
        if (text.Trim() == "[DONE]") { Terminal = true; if (!HasError) State = "completed"; return; }
        try
        {
            using var doc = JsonDocument.Parse(text);
            Observe(doc.RootElement, eventType);
        }
        catch (JsonException ex) { throw new IOException("上游事件内容无效", ex); }
    }

    private void Error(JsonElement e)
    {
        HasError = true;
        var text = e.ValueKind == JsonValueKind.String ? e.GetString()! :
            $"{Text(e, "code")} {Text(e, "type")} {Text(e, "message")}";
        string[] permanent = ["insufficient_quota", "insufficient_balance", "credit balance", "account_deactivated",
            "account_disabled", "billing_hard_limit", "billing_not_active", "exceeded your current quota",
            "quota_exhausted", "余额不足", "账号停用", "quota exhausted"];
        string[] limited = ["rate_limit", "rate limit", "too many requests", "限流"];
        PermanentError |= permanent.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        RateLimited |= limited.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private void Observe(JsonElement e, string eventType = "")
    {
        if (e.ValueKind != JsonValueKind.Object) throw new IOException("上游响应必须为 JSON 对象");
        var type = Text(e, "type");
        if (type.Length == 0) type = eventType;
        var status = Text(e, "status");
        if (e.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) HasUsage = true;
        if (e.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) Error(error);
        if (type is "error" or "response.error" or "response.failed" || status == "failed")
        { Error(e); Terminal = true; State = "failed"; }
        if (type is "response.incomplete" or "response.cancelled" or "response.canceled" || status is "incomplete" or "cancelled")
        { HasError = true; Terminal = true; State = "incomplete"; }
        if (e.TryGetProperty("incomplete_details", out var incomplete))
            IncompleteReason = Text(incomplete, "reason");
        if (type is "response.completed" or "response.done" or "message_stop" || status == "completed")
        { Terminal = true; if (!HasError) State = "completed"; }
        if (e.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object) Observe(response);
        if (e.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object) Observe(message);
        if (e.TryGetProperty("delta", out var delta))
        {
            if (delta.ValueKind == JsonValueKind.String && delta.GetString()!.Length > 0) HasOutput = true;
            if (delta.ValueKind == JsonValueKind.Object) Output(delta);
        }
        Output(e);
        if (e.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            foreach (var choice in choices.EnumerateArray())
            {
                Observe(choice);
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
                { Terminal = true; if (!HasError) State = "completed"; }
            }
        foreach (var key in new[] { "output", "content", "summary" })
            if (e.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) Observe(item);
        foreach (var key in new[] { "item", "content_block" })
            if (e.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object) Observe(item);
    }

    private void Output(JsonElement e)
    {
        foreach (var key in new[] { "text", "content", "thinking", "reasoning", "reasoning_content", "refusal", "partial_json", "arguments", "encrypted_content" })
            if (Text(e, key).Length > 0) HasOutput = true;
        var type = Text(e, "type");
        if (type == "tool_use" || type.EndsWith("_call", StringComparison.Ordinal))
            HasOutput = true;
        foreach (var key in new[] { "tool_calls", "function_call" })
            if (e.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 0)) HasOutput = true;
    }
}
