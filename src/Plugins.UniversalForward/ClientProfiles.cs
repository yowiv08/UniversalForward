using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>客户端请求身份与连接测试协议。</summary>
internal static class ClientProfiles
{
    private static readonly string[] SessionHeaders = ["Session-Id", "Thread-Id", "x-claude-code-session-id"];
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
        IReadOnlyDictionary<string, string>? incoming = null, ClientSessionIdentity? fallback = null,
        JsonObject? configuration = null, IReadOnlyDictionary<string, string>? legacyOverrides = null)
    {
        string? Header(string name) => ReadHeader(incoming, name);
        string? Override(string name) => ConfiguredValue(configuration, incoming, name) ?? ReadHeader(legacyOverrides, name);
        var meta = body?["client_metadata"] as JsonObject;
        var claudeIdentity = ParseObject(Read((body?["metadata"] as JsonObject)?["user_id"]));
        var suppliedMetadata = Override("X-Codex-Turn-Metadata") ?? Header("x-codex-turn-metadata")
            ?? Read(meta?["x-codex-turn-metadata"]);
        var turnMetadata = ParseObject(suppliedMetadata);
        var suppliedThread = Override("Thread-Id") ?? Header("thread-id") ?? Read(meta?["thread_id"])
            ?? Read(turnMetadata?["thread_id"]);
        var session = Override("Session-Id") ?? Override("x-claude-code-session-id")
            ?? Header("session-id") ?? Header("x-claude-code-session-id")
            ?? Read(meta?["session_id"]) ?? Read(claudeIdentity?["session_id"]) ?? Read(turnMetadata?["session_id"])
            ?? suppliedThread ?? fallback?.SessionId ?? Id();
        var turn = Read(meta?["turn_id"]) ?? Read(turnMetadata?["turn_id"]) ?? Id();
        var installation = Read(meta?["x-codex-installation-id"]) ?? Read(turnMetadata?["installation_id"])
            ?? fallback?.InstallationId ?? Id();
        var thread = suppliedThread ?? fallback?.ThreadId ?? session;
        var window = Override("X-Codex-Window-Id") ?? Header("x-codex-window-id")
            ?? Read(meta?["x-codex-window-id"]) ?? Read(turnMetadata?["window_id"]) ?? fallback?.WindowId ?? session + ":0";
        var context = Read(turnMetadata?["context_window_id"]) ?? fallback?.ContextWindowId ?? Id();
        var metadata = suppliedMetadata
            ?? new JsonObject
            {
                ["installation_id"] = installation, ["session_id"] = session, ["thread_id"] = thread,
                ["agent_name"] = "/root", ["turn_id"] = turn, ["window_id"] = window, ["window_number"] = 0,
                ["context_window_id"] = context, ["request_kind"] = "turn",
                ["root_turn_id"] = Read(meta?["root_turn_id"]) ?? turn,
                ["thread_source"] = "user", ["sandbox"] = "none", ["sandbox_mode"] = "read-only",
                ["auto_review_enabled"] = false, ["node_repl_auto_review_required"] = false,
                ["node_repl_disabled"] = false, ["turn_started_at_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.ToJsonString();
        return new ClientProfileVariables
        {
            GeneratedTurnMetadata = suppliedMetadata is null,
            ["request_id"] = Override("X-Client-Request-Id") ?? Header("x-client-request-id") ?? Id(),
            ["session_id"] = session, ["thread_id"] = thread, ["turn_id"] = turn,
            ["installation_id"] = installation, ["context_window_id"] = context, ["window_id"] = window,
            ["codex_turn_metadata"] = metadata,
            ["device_id"] = Read(claudeIdentity?["device_id"]) ?? fallback?.DeviceId
                ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        };
    }

    internal static string? IdentitySource(JsonNode? body, IReadOnlyDictionary<string, string> incoming,
        JsonObject configuration, IReadOnlyDictionary<string, string>? legacyOverrides = null)
    {
        string? Override(string name) => ConfiguredValue(configuration, incoming, name) ?? ReadHeader(legacyOverrides, name);
        if (SessionHeaders.Any(name => Override(name) is not null)
            || HasSession(ParseObject(Override("X-Codex-Turn-Metadata"))))
            return "override";
        if (SessionHeaders.Any(name => ReadHeader(incoming, name) is not null)
            || HasSession(body?["client_metadata"] as JsonObject)
            || HasSession(ParseObject(Read((body?["metadata"] as JsonObject)?["user_id"])))
            || HasSession(ParseObject(ReadHeader(incoming, "X-Codex-Turn-Metadata")))
            || HasSession(ParseObject(Read((body?["client_metadata"] as JsonObject)?["x-codex-turn-metadata"]))))
            return "client";
        return null;
    }

    private static bool HasSession(JsonObject? metadata) => Read(metadata?["session_id"]) is not null || Read(metadata?["thread_id"]) is not null;
    private static string? Read(JsonNode? value) => value is not null && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString() : null;
    private static string? ReadHeader(IReadOnlyDictionary<string, string>? headers, string name)
    {
        var value = headers?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    private static JsonObject? ParseObject(string? text)
    {
        if (text is null) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
    private static string? ConfiguredValue(JsonObject? config, IReadOnlyDictionary<string, string>? incoming, string name)
    {
        if (config?.FirstOrDefault(pair => pair.Key.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Value
            is not JsonValue node || !node.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return null;
        const string clientPrefix = "{client_header:";
        var trimmed = text.Trim();
        if (trimmed.StartsWith(clientPrefix, StringComparison.Ordinal) && trimmed.EndsWith('}'))
            return ReadHeader(incoming, trimmed[clientPrefix.Length..^1].Trim());
        // A generated placeholder is not a client-provided conversation boundary.
        return System.Text.RegularExpressions.Regex.IsMatch(text, @"\{[a-z_]+\}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? null : text;
    }

    internal static void AlignGeneratedMetadata(Dictionary<string, string> headers, IReadOnlyDictionary<string, string> variables)
    {
        if (variables is not ClientProfileVariables { GeneratedTurnMetadata: true }
            || !headers.TryGetValue("X-Codex-Turn-Metadata", out var text)
            || text != variables["codex_turn_metadata"]) return;
        var metadata = JsonNode.Parse(text)!.AsObject();
        foreach (var (field, header) in new[] { ("session_id", "Session-Id"), ("thread_id", "Thread-Id"), ("window_id", "X-Codex-Window-Id") })
            if (headers.TryGetValue(header, out var value)) metadata[field] = value;
        headers["X-Codex-Turn-Metadata"] = metadata.ToJsonString();
    }

    internal static JsonObject CompleteHeaders(JsonObject config, IReadOnlyDictionary<string, string>? incoming = null)
    {
        var result = (JsonObject)config.DeepClone();
        void Add(string name, string value)
        {
            if (!result.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                result[name] = ReadHeader(incoming, name) is not null
                    ? "{client_header:" + name + "}" : value;
        }
        if (Profile(config) == "codex")
        {
            Add("Session-Id", "{session_id}");
            Add("Thread-Id", "{thread_id}");
            Add("X-Client-Request-Id", "{request_id}");
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

    internal static JsonObject PrepareClaudeRequest(JsonObject original, Dictionary<string, string> headers, string? deviceId = null)
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
        identity["device_id"] ??= deviceId ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
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

internal sealed class ClientProfileVariables : Dictionary<string, string>
{
    internal bool GeneratedTurnMetadata { get; init; }
}
