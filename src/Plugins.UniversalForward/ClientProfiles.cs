using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>客户端请求身份与连接测试协议。</summary>
internal static class ClientProfiles
{
    private static string Id() => Guid.NewGuid().ToString();
    private static readonly Lazy<JsonObject> Codex = new(() =>
    {
        using var stream = typeof(ClientProfiles).Assembly.GetManifestResourceStream(
            "Plugins.UniversalForward.Contracts.codex-protocol-defaults.json")!;
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

    internal static JsonObject PrepareCodexRequest(JsonObject original, IReadOnlyDictionary<string, string> headers)
    {
        var body = (JsonObject)original.DeepClone();
        foreach (var name in new[] { "tool_choice", "parallel_tool_calls", "reasoning", "store", "text" })
            if (body[name] is null) body[name] = Codex.Value["body"]![name]!.DeepClone();
        if (body["stream"] is null) body["stream"] = false;
        if (body["input"] is JsonValue input && input.TryGetValue<string>(out var text))
            body["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text })
            });
        if (body["include"] is not null and not JsonArray)
            throw new FormatException("Codex include 必须为数组");
        var include = body["include"] as JsonArray ?? new JsonArray();
        if (!include.Any(x => x is JsonValue value && value.TryGetValue<string>(out var name) && name == "reasoning.encrypted_content"))
            include.Add("reasoning.encrypted_content");
        if (body["include"] is null) body["include"] = include;
        if (body["prompt_cache_key"] is null) body["prompt_cache_key"] = headers["Session-Id"];
        if (body["client_metadata"] is not null and not JsonObject)
            throw new FormatException("Codex client_metadata 必须为对象");
        var client = body["client_metadata"] as JsonObject ?? new JsonObject();
        if (body["client_metadata"] is null) body["client_metadata"] = client;
        var metadata = JsonNode.Parse(headers["X-Codex-Turn-Metadata"]) as JsonObject
            ?? throw new FormatException("Codex 回合元数据必须为对象");
        client["session_id"] = headers["Session-Id"];
        client["thread_id"] = headers["Thread-Id"];
        client["x-codex-window-id"] = headers["X-Codex-Window-Id"];
        client["x-codex-turn-metadata"] = headers["X-Codex-Turn-Metadata"];
        foreach (var (target, source) in new[]
        {
            ("turn_id", "turn_id"), ("root_turn_id", "root_turn_id"), ("x-codex-installation-id", "installation_id")
        })
            if (client[target] is null && metadata[source] is { } value) client[target] = value.DeepClone();
        return body;
    }

    internal static JsonObject PrepareClaudeRequest(JsonObject original, Dictionary<string, string> headers)
    {
        const string identityText = "You are a Claude agent, built on Anthropic's Claude Agent SDK.";
        var body = (JsonObject)original.DeepClone();
        if (body["model"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var model)
            && model.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase))
        {
            body["model"] = model[..^4];
            var beta = headers.GetValueOrDefault("anthropic-beta", "");
            if (!beta.Split(',', StringSplitOptions.TrimEntries).Contains("context-1m-2025-08-07"))
                headers["anthropic-beta"] = string.IsNullOrEmpty(beta) ? "context-1m-2025-08-07" : beta + ",context-1m-2025-08-07";
        }
        if (body["stream"] is null) body["stream"] = false;
        var system = body["system"] switch
        {
            null => new JsonArray(),
            JsonArray array => (JsonArray)array.DeepClone(),
            JsonValue value when value.TryGetValue<string>(out var text) =>
                new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            _ => throw new FormatException("Claude system 必须为字符串或数组")
        };
        if (!system.OfType<JsonObject>().Any(x => x["text"] is JsonValue text && text.TryGetValue<string>(out var value)
            && (value.StartsWith(identityText, StringComparison.Ordinal) || value.StartsWith("You are Claude Code,", StringComparison.Ordinal))))
            system.Insert(0, new JsonObject { ["type"] = "text", ["text"] = identityText });
        body["system"] = system;
        if (body["messages"] is JsonArray messages)
            foreach (var message in messages.OfType<JsonObject>())
                if (message["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                    message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        if (body["metadata"] is not null and not JsonObject)
            throw new FormatException("Claude metadata 必须为对象");
        var metadata = body["metadata"] as JsonObject ?? new JsonObject();
        if (body["metadata"] is null) body["metadata"] = metadata;
        JsonObject? identity = null;
        if (metadata["user_id"] is { } userId)
        {
            if (userId is not JsonValue value || !value.TryGetValue<string>(out var text))
                throw new FormatException("Claude metadata.user_id 必须为字符串");
            try { identity = JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { }
            identity ??= new JsonObject { ["user_id"] = text };
        }
        identity ??= new JsonObject();
        identity["device_id"] ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        identity["account_uuid"] ??= "";
        identity["session_id"] = headers["x-claude-code-session-id"];
        metadata["user_id"] = identity.ToJsonString();
        return body;
    }

    internal static JsonObject TestBody(string profile, string model, Dictionary<string, string> headers)
    {
        if (profile == "claude")
            return PrepareClaudeRequest(new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Reply only OK." })
                }),
                ["system"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text", ["text"] = "Reply only OK."
                }),
                ["max_tokens"] = 32, ["stream"] = true
            }, headers);

        return PrepareCodexRequest(new JsonObject
        {
            ["model"] = model, ["stream"] = true, ["instructions"] = "Reply only OK. Do not call tools.",
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Reply only OK." })
            })
        }, headers);
    }
}
