using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ClientProfilesTests
{
    [TestMethod]
    public void CodexLegacyTemplateGetsFreshCompleteIdentityAndLightweightBody()
    {
        var config = new JsonObject { ["Originator"] = "codex_exec", ["Authorization"] = "Bearer {api_key}" };
        var headers = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "secret", true);
        Assert.AreEqual("Bearer secret", headers["Authorization"]);
        Assert.IsTrue(Guid.TryParse(headers["Session-Id"], out _));
        Assert.AreEqual(headers["Session-Id"], headers["Thread-Id"]);
        Assert.IsTrue(Guid.TryParse(headers["X-Client-Request-Id"], out _));
        Assert.AreNotEqual(headers["Session-Id"], headers["X-Client-Request-Id"]);
        Assert.AreEqual(headers["Session-Id"] + ":0", headers["X-Codex-Window-Id"]);
        var next = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "secret", true);
        Assert.AreNotEqual(headers["Session-Id"], next["Session-Id"]);
        Assert.AreEqual(2, config.Count);
        var body = ClientProfiles.TestBody("codex", "test{{SESSION_ID}}", headers);
        Assert.AreEqual("test{{SESSION_ID}}", body["model"]!.ToString());
        Assert.IsTrue(body["stream"]!.GetValue<bool>());
        Assert.IsFalse(body["store"]!.GetValue<bool>());
        Assert.IsNull(body["max_output_tokens"]);
        Assert.AreEqual(1, body["input"]!.AsArray().Count);
        Assert.IsFalse(body["input"]!.ToJsonString().Contains("additional_tools", StringComparison.Ordinal));
        Assert.AreEqual(headers["Session-Id"], body["prompt_cache_key"]!.ToString());
        Assert.AreEqual(headers["X-Codex-Turn-Metadata"], body["client_metadata"]!["x-codex-turn-metadata"]!.ToString());
        body["model"] = "test";
        Assert.IsFalse(body.ToJsonString().Contains("{{", StringComparison.Ordinal));
        Assert.IsFalse(headers.ContainsKey("Host"));
        Assert.IsFalse(headers.ContainsKey("Content-Length"));
    }

    [TestMethod]
    public void ClaudeProbeMatchesSessionAndDoesNotAdvertiseUnimplementedTools()
    {
        var config = JsonNode.Parse("""{"x-app":"cli","anthropic-beta":"claude-code-20250219"}""")!.AsObject();
        var headers = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "secret", true);
        Assert.AreEqual("true", headers["anthropic-dangerous-direct-browser-access"]);
        var body = ClientProfiles.TestBody("claude", "claude-test", headers);
        var identity = JsonNode.Parse(body["metadata"]!["user_id"]!.ToString())!;
        Assert.AreEqual(headers["x-claude-code-session-id"], identity["session_id"]!.ToString());
        Assert.AreEqual(64, identity["device_id"]!.ToString().Length);
        Assert.IsTrue(body["stream"]!.GetValue<bool>());
        Assert.AreEqual(32, body["max_tokens"]!.GetValue<int>());
        Assert.IsNull(body["tools"]);
    }

    [TestMethod]
    public void BodyIdentityIsPreservedAndValuesAreExpandedOnce()
    {
        var body = JsonNode.Parse("""
            {"client_metadata":{"session_id":"body-session","thread_id":"body-thread","x-codex-turn-metadata":"{\"session_id\":\"body-session\"}"}}
            """)!;
        var before = body.ToJsonString();
        var variables = ClientProfiles.Variables(body);
        var config = new JsonObject { ["Originator"] = "codex_exec", ["Authorization"] = "Bearer {api_key}" };
        var headers = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "{session_id}", false, variables);
        Assert.AreEqual("body-session", headers["Session-Id"]);
        Assert.AreEqual("body-thread", headers["Thread-Id"]);
        Assert.AreEqual("Bearer {session_id}", headers["Authorization"]);
        Assert.AreEqual(before, body.ToJsonString());
    }

    [TestMethod]
    public void ExplicitHeadersWinAndControlCharactersAreRejected()
    {
        var config = new JsonObject { ["Originator"] = "codex_exec", ["session-id"] = "custom" };
        var headers = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "key");
        Assert.AreEqual("custom", headers["session-id"]);
        Assert.Throws<FormatException>(() => HeaderOverrides.Resolve(config,
            new Dictionary<string, string> { ["x-codex-turn-metadata"] = "bad\r\nInjected: yes" }, "key"));
    }

    [TestMethod]
    public void DeletedCodexOverridesUseClientIdentityBeforeGeneratedDefaults()
    {
        var client = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["session-id"] = "client-session", ["thread-id"] = "client-thread",
            ["X-Client-Request-Id"] = "client-request", ["X-Codex-Window-Id"] = "client-window",
            ["X-Codex-Turn-Metadata"] = "{\"custom\":\"client-metadata\"}"
        };
        var config = new JsonObject { ["Originator"] = "codex_exec", ["Session-Id"] = "configured-session" };
        var bodyIdentity = ClientProfiles.Variables(JsonNode.Parse("""{"client_metadata":{"session_id":"body-session"}}"""));
        var configured = HeaderOverrides.Resolve(config, client, "key", variables: bodyIdentity);
        Assert.AreEqual("configured-session", configured["Session-Id"]);
        config.Remove("Session-Id");
        var restored = HeaderOverrides.Resolve(config, client, "key", variables: bodyIdentity);
        foreach (var (name, value) in client) Assert.AreEqual(value, restored[name], name);
        var test = HeaderOverrides.Resolve(config, client, "key", channelTest: true);
        Assert.AreNotEqual("client-session", test["Session-Id"]);
        Assert.AreNotEqual("client-request", test["X-Client-Request-Id"]);
    }

    [TestMethod]
    public void GeneratedTemplatePlaceholdersStillAllowInferredSessionIdentity()
    {
        var config = new JsonObject
        {
            ["Originator"] = "codex_exec", ["Session-Id"] = "{session_id}", ["Thread-Id"] = "{thread_id}",
            ["X-Codex-Turn-Metadata"] = "{codex_turn_metadata}"
        };
        var client = new Dictionary<string, string>();
        var body = JsonNode.Parse("""{"input":"question"}""")!;
        Assert.IsNull(ClientProfiles.IdentitySource(body, client, config));
        var inferred = ClientSessionIdentity.Create();
        var variables = ClientProfiles.Variables(body, client, inferred, config);
        var headers = HeaderOverrides.Resolve(config, client, "key", variables: variables);
        Assert.AreEqual(inferred.SessionId, headers["Session-Id"]);
        Assert.AreEqual(inferred.ThreadId, headers["Thread-Id"]);
    }

    [TestMethod]
    public void ConfiguredClientHeaderAndMetadataOnlyIdentityArePreserved()
    {
        var config = new JsonObject { ["Originator"] = "codex_exec", ["Session-Id"] = "{client_header:X-Chat}" };
        var client = new Dictionary<string, string> { ["X-Chat"] = "client-session" };
        Assert.AreEqual("override", ClientProfiles.IdentitySource(null, client, config));
        var headers = HeaderOverrides.Resolve(config, client, "key",
            variables: ClientProfiles.Variables(incoming: client, configuration: config));
        Assert.AreEqual("client-session", headers["Session-Id"]);
        Assert.AreEqual("client-session", JsonNode.Parse(headers["X-Codex-Turn-Metadata"])!["session_id"]!.ToString());

        config.Remove("Session-Id");
        const string metadata = """{"session_id":"metadata-session","thread_id":"metadata-thread","turn_id":"native-turn","custom":"untouched"}""";
        client = new Dictionary<string, string> { ["X-Codex-Turn-Metadata"] = metadata };
        Assert.AreEqual("client", ClientProfiles.IdentitySource(null, client, config));
        var resolved = HeaderOverrides.Resolve(config, client, "key");
        Assert.AreEqual("metadata-session", resolved["Session-Id"]);
        Assert.AreEqual("metadata-thread", resolved["Thread-Id"]);
        Assert.AreEqual(metadata, resolved["X-Codex-Turn-Metadata"]);
    }

    [TestMethod]
    public void EmptyClientIdentityValuesAreCompleted()
    {
        var config = new JsonObject { ["Originator"] = "codex_exec" };
        var client = new Dictionary<string, string>
        {
            ["Session-Id"] = "", ["Thread-Id"] = " ", ["X-Client-Request-Id"] = "", ["X-Codex-Turn-Metadata"] = ""
        };
        Assert.IsNull(ClientProfiles.IdentitySource(null, client, config));
        var headers = HeaderOverrides.Resolve(config, client, "key");
        Assert.IsTrue(Guid.TryParse(headers["Session-Id"], out _));
        Assert.IsTrue(Guid.TryParse(headers["X-Client-Request-Id"], out _));
        Assert.AreEqual(headers["Session-Id"], headers["Thread-Id"]);
    }

    [TestMethod]
    public void DeletedClaudeOverridesUseClientValuesBeforeGeneratedDefaults()
    {
        var config = new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" };
        var client = new Dictionary<string, string>
        {
            ["x-claude-code-session-id"] = "original-session",
            ["anthropic-dangerous-direct-browser-access"] = "false"
        };
        var resolved = HeaderOverrides.Resolve(config, client, "key");
        foreach (var (name, value) in client) Assert.AreEqual(value, resolved[name], name);
    }

    [TestMethod]
    public void CodexCompletionPreservesConversationToolsAndExplicitOptions()
    {
        var original = JsonNode.Parse("""
            {"model":"model","stream":false,"instructions":"Keep {{SESSION_ID}} verbatim.",
             "input":[{"role":"user","content":[{"type":"input_text","text":"my question"},{"type":"input_image","image_url":"data:image/png;base64,AA=="}]},
                      {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"q\":1}"},
                      {"type":"function_call_output","call_id":"call_1","output":"result"}],
             "tools":[{"type":"function","name":"lookup","parameters":{"type":"object"}}],
             "tool_choice":"required","parallel_tool_calls":true,"reasoning":{"effort":"high"},
             "max_output_tokens":128,"text":{"format":{"type":"json_object"}},
             "include":["message.output_text.logprobs"],"prompt_cache_key":"existing-cache",
             "client_metadata":{"custom":"preserved"},"unknown":{"x":1}}
            """)!.AsObject();
        var snapshot = original.ToJsonString();
        var headers = HeaderOverrides.Resolve(new JsonObject { ["Originator"] = "codex_exec" }, new Dictionary<string, string>(), "key",
            variables: ClientProfiles.Variables(original));
        var body = ClientProfiles.PrepareCodexRequest(original, headers);
        foreach (var name in new[] { "model", "stream", "instructions", "input", "tools", "tool_choice", "parallel_tool_calls",
            "reasoning", "max_output_tokens", "text", "prompt_cache_key", "unknown" })
            Assert.IsTrue(JsonNode.DeepEquals(original[name], body[name]), name);
        Assert.AreEqual("preserved", body["client_metadata"]!["custom"]!.ToString());
        Assert.AreEqual(headers["Session-Id"], body["client_metadata"]!["session_id"]!.ToString());
        Assert.AreEqual(headers["X-Codex-Turn-Metadata"], body["client_metadata"]!["x-codex-turn-metadata"]!.ToString());
        Assert.AreEqual(2, body["include"]!.AsArray().Count);
        Assert.IsTrue(JsonNode.DeepEquals(body, ClientProfiles.PrepareCodexRequest(body, headers)));
        Assert.AreEqual(snapshot, original.ToJsonString());
    }

    [TestMethod]
    public void CodexCompletionNormalizesStringInputAndEmptyInclude()
    {
        var original = new JsonObject { ["input"] = "Actual request {api_key}", ["include"] = new JsonArray() };
        var headers = HeaderOverrides.Resolve(new JsonObject { ["Originator"] = "codex_exec" }, new Dictionary<string, string>(), "key");
        var body = ClientProfiles.PrepareCodexRequest(original, headers);
        Assert.AreEqual("Actual request {api_key}", body["input"]![0]!["content"]![0]!["text"]!.ToString());
        Assert.AreEqual("reasoning.encrypted_content", body["include"]![0]!.ToString());
        Assert.IsFalse(body["stream"]!.GetValue<bool>());
        Assert.IsNull(body["tools"]);
        Assert.IsNull(body["instructions"]);
        Assert.AreEqual("Actual request {api_key}", original["input"]!.ToString());
    }

    [TestMethod]
    public void ProbeAndOrdinaryRequestsUseSameCodexCompletion()
    {
        var original = new JsonObject
        {
            ["model"] = "model", ["stream"] = true, ["instructions"] = "Reply only OK. Do not call tools.",
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Reply only OK." })
            })
        };
        var headers = HeaderOverrides.Resolve(new JsonObject { ["Originator"] = "codex_exec" }, new Dictionary<string, string>(), "key");
        Assert.IsTrue(JsonNode.DeepEquals(ClientProfiles.TestBody("codex", "model", headers),
            ClientProfiles.PrepareCodexRequest(original, headers)));
    }

    [TestMethod]
    public void ClaudeCompletionPreservesNativeIdentitySystemAndTools()
    {
        var original = JsonNode.Parse("""
            {"model":"vendor/future-model","stream":false,"max_tokens":123,
             "system":[{"type":"text","text":"You are Claude Code, a coding assistant."},{"type":"text","text":"My system prompt","cache_control":{"type":"ephemeral"}}],
             "messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"result"}]}],
             "tools":[{"name":"lookup","input_schema":{"type":"object"}}],"tool_choice":{"type":"any"},
             "thinking":{"type":"adaptive"},"output_config":{"effort":"high"},
             "metadata":{"custom":"keep","user_id":"{\"device_id\":\"existing-device\",\"session_id\":\"existing-session\",\"account_uuid\":\"existing-account\",\"other\":1}"}}
            """)!.AsObject();
        var snapshot = original.ToJsonString();
        var config = new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" };
        var headers = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "key", variables: ClientProfiles.Variables(original));
        var body = ClientProfiles.PrepareClaudeRequest(original, headers);
        Assert.IsTrue(JsonNode.DeepEquals(original, body));
        Assert.AreEqual("existing-session", headers["x-claude-code-session-id"]);
        Assert.IsTrue(JsonNode.DeepEquals(body, ClientProfiles.PrepareClaudeRequest(body, headers)));
        Assert.AreEqual(snapshot, original.ToJsonString());
    }

    [TestMethod]
    public void ClaudeCompletionAddsIdentityWithoutReplacingUserInstructions()
    {
        var original = JsonNode.Parse("""
            {"model":"any-new-model","system":"My {{instructions}}","stream":true,"max_tokens":17,
             "messages":[{"role":"user","content":"My question"}],"metadata":{"user_id":"app-user"}}
            """)!.AsObject();
        var headers = HeaderOverrides.Resolve(new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" },
            new Dictionary<string, string>(), "key");
        var body = ClientProfiles.PrepareClaudeRequest(original, headers);
        Assert.AreEqual("any-new-model", body["model"]!.ToString());
        Assert.AreEqual("My {{instructions}}", body["system"]![1]!["text"]!.ToString());
        Assert.AreEqual("My question", body["messages"]![0]!["content"]![0]!["text"]!.ToString());
        Assert.AreEqual(17, body["max_tokens"]!.GetValue<int>());
        Assert.IsNull(body["tools"]);
        Assert.IsNull(body["thinking"]);
        var identity = JsonNode.Parse(body["metadata"]!["user_id"]!.ToString())!;
        Assert.AreEqual("app-user", identity["user_id"]!.ToString());
        Assert.AreEqual(headers["x-claude-code-session-id"], identity["session_id"]!.ToString());
        Assert.IsTrue(JsonNode.DeepEquals(body, ClientProfiles.PrepareClaudeRequest(body, headers)));
    }

    [TestMethod]
    public void ClaudeOneMillionSuffixWorksForAnyModelInProbeAndNormalRequest()
    {
        var headers = HeaderOverrides.Resolve(new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" },
            new Dictionary<string, string>(), "key");
        var body = ClientProfiles.PrepareClaudeRequest(new JsonObject { ["model"] = "vendor/future[1M]" }, headers);
        Assert.AreEqual("vendor/future", body["model"]!.ToString());
        Assert.AreEqual("claude-code-20250219,context-1m-2025-08-07", headers["anthropic-beta"]);
        var probe = ClientProfiles.TestBody("claude", "another-future[1m]", headers);
        Assert.AreEqual("another-future", probe["model"]!.ToString());
        Assert.AreEqual("claude-code-20250219,context-1m-2025-08-07", headers["anthropic-beta"]);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("claude")]
    public void ProfilesDoNotDependOnModelNames(string profile)
    {
        var model = "vendor/" + Guid.NewGuid().ToString("N");
        var config = profile == "codex" ? new JsonObject { ["Originator"] = "codex_exec" }
            : new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" };
        var headers = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "key");
        Assert.AreEqual(model, ClientProfiles.TestBody(profile, model, headers)["model"]!.ToString());
        var original = new JsonObject { ["model"] = model };
        var body = profile == "codex" ? ClientProfiles.PrepareCodexRequest(original, headers)
            : ClientProfiles.PrepareClaudeRequest(original, headers);
        Assert.AreEqual(model, body["model"]!.ToString());
    }
}
