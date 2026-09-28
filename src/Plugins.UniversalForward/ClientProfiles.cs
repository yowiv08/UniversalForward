using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Plugins.UniversalForward;

/// <summary>客户端请求身份与连接测试协议。</summary>
internal static class ClientProfiles
{
    private static string Id() => Guid.NewGuid().ToString();
    private static readonly Lazy<JsonObject> Codex = new(() =>
    {
        using var stream = typeof(ClientProfiles).Assembly.GetManifestResourceStream(
            "Plugins.UniversalForward.Contracts.codex-cli-0.155.1-request.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    });

    internal static string? Profile(JsonObject config)
    {
        string? Read(string name) => config.FirstOrDefault(p =>
            p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
        if (Read("Originator") == "codex_exec") return "codex";
        if (Read("x-app") == "cli" && Read("anthropic-beta")?.Contains("claude-code-", StringComparison.Ordinal) == true)
            return "claude";
        return null;
    }

    internal static Dictionary<string, string> Variables(JsonNode? body = null,
        IReadOnlyDictionary<string, string>? incoming = null)
    {
        string? Header(string name) => incoming?.FirstOrDefault(p =>
            p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var meta = body?["client_metadata"] as JsonObject;
        JsonObject? claudeIdentity = null;
        if (body?["metadata"] is JsonObject claudeMeta && claudeMeta["user_id"] is JsonValue userId
            && userId.TryGetValue<string>(out var userText))
        {
            try { claudeIdentity = JsonNode.Parse(userText) as JsonObject; }
            catch (System.Text.Json.JsonException) { }
        }
        var session = meta?["session_id"]?.ToString() ?? claudeIdentity?["session_id"]?.ToString()
            ?? Header("session-id") ?? Header("x-claude-code-session-id") ?? Id();
        var turn = meta?["turn_id"]?.ToString() ?? Id();
        var installation = meta?["x-codex-installation-id"]?.ToString() ?? Id();
        var thread = meta?["thread_id"]?.ToString() ?? Header("thread-id") ?? session;
        var window = meta?["x-codex-window-id"]?.ToString() ?? Header("x-codex-window-id") ?? session + ":0";
        var context = Id();
        var metadata = meta?["x-codex-turn-metadata"]?.ToString() ?? Header("x-codex-turn-metadata")
            ?? new JsonObject
            {
                ["installation_id"] = installation, ["session_id"] = session, ["thread_id"] = thread,
                ["agent_name"] = "/root", ["turn_id"] = turn, ["window_id"] = window, ["window_number"] = 0,
                ["context_window_id"] = context, ["request_kind"] = "turn", ["root_turn_id"] = turn,
                ["thread_source"] = "user", ["sandbox"] = "none", ["sandbox_mode"] = "read-only",
                ["auto_review_enabled"] = false, ["node_repl_auto_review_required"] = false,
                ["node_repl_disabled"] = false, ["turn_started_at_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.ToJsonString();
        return new(StringComparer.Ordinal)
        {
            ["session_id"] = session, ["thread_id"] = thread, ["turn_id"] = turn,
            ["installation_id"] = installation, ["context_window_id"] = context, ["window_id"] = window,
            ["codex_turn_metadata"] = metadata
        };
    }

    internal static JsonObject CompleteHeaders(JsonObject config)
    {
        var result = (JsonObject)config.DeepClone();
        void Add(string name, string value)
        {
            if (!result.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase))) result[name] = value;
        }
        if (Profile(config) == "codex")
        {
            Add("Session-Id", "{session_id}");
            Add("Thread-Id", "{thread_id}");
            Add("X-Client-Request-Id", "{session_id}");
            Add("X-Codex-Window-Id", "{window_id}");
            Add("X-Codex-Turn-Metadata", "{codex_turn_metadata}");
        }
        if (Profile(config) == "claude")
        {
            Add("x-claude-code-session-id", "{session_id}");
            Add("anthropic-dangerous-direct-browser-access", "true");
        }
        return result;
    }

    internal static JsonObject TestBody(string profile, string model, IReadOnlyDictionary<string, string> headers)
    {
        if (profile == "claude")
            return new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Reply only OK." })
                }),
                ["system"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text", ["text"] = "You are a Claude agent, built on Anthropic's Claude Agent SDK. Reply only OK."
                }),
                ["metadata"] = new JsonObject
                {
                    ["user_id"] = new JsonObject
                    {
                        ["device_id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                        ["account_uuid"] = "", ["session_id"] = headers["x-claude-code-session-id"]
                    }.ToJsonString()
                },
                ["max_tokens"] = 32, ["stream"] = true
            };

        var metadata = JsonNode.Parse(headers["X-Codex-Turn-Metadata"])!.AsObject();
        var slots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MODEL"] = model, ["PROMPT"] = "Reply only OK. Do not call tools.",
            ["SESSION_ID"] = headers["Session-Id"],
            ["TURN_ID"] = metadata["turn_id"]!.ToString(),
            ["INSTALLATION_ID"] = metadata["installation_id"]!.ToString(),
            ["CONTEXT_WINDOW_ID"] = metadata["context_window_id"]!.ToString(),
            ["CURRENT_DATE"] = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        foreach (var name in new[] { "TOOLS_ITEM_ID", "BASE_MESSAGE_ID", "CONTEXT_MESSAGE_ID",
            "COLLABORATION_MESSAGE_ID", "MODE_MESSAGE_ID", "ENVIRONMENT_MESSAGE_ID", "USER_MESSAGE_ID" })
            slots[name] = Id();
        var body = Expand(Codex.Value["body"], slots)!.AsObject();
        body["stream"] = true;
        var client = body["client_metadata"]!.AsObject();
        client["x-codex-turn-metadata"] = headers["X-Codex-Turn-Metadata"];
        client["thread_id"] = headers["Thread-Id"];
        client["x-codex-window-id"] = headers["X-Codex-Window-Id"];
        var lastDeveloper = body["input"]!.AsArray().Last(x => x?["role"]?.ToString() == "developer")!;
        lastDeveloper["content"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = "This is a connection test without tool execution. Reply only OK. Do not call tools."
        });
        return body;
    }

    private static JsonNode? Expand(JsonNode? node, Dictionary<string, string> slots) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Expand(p.Value, slots)))),
        JsonArray array => new JsonArray(array.Select(p => Expand(p, slots)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) =>
            JsonValue.Create(Regex.Replace(text, @"\{\{([A-Z_]+)\}\}", m =>
                slots.TryGetValue(m.Groups[1].Value, out var replacement) ? replacement : m.Value)),
        _ => node?.DeepClone()
    };
}
