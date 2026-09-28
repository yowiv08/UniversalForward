using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ClientProfilesTests
{
    [TestMethod]
    public void CodexLegacyTemplateGetsFreshCompleteIdentityAndNativeBody()
    {
        var config = new JsonObject { ["Originator"] = "codex_exec", ["Authorization"] = "Bearer {api_key}" };
        var headers = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "secret", true);
        Assert.AreEqual("Bearer secret", headers["Authorization"]);
        Assert.IsTrue(Guid.TryParse(headers["Session-Id"], out _));
        Assert.AreEqual(headers["Session-Id"], headers["Thread-Id"]);
        Assert.AreEqual(headers["Session-Id"], headers["X-Client-Request-Id"]);
        Assert.AreEqual(headers["Session-Id"] + ":0", headers["X-Codex-Window-Id"]);
        var next = HeaderOverrides.Resolve(config, new Dictionary<string, string>(), "secret", true);
        Assert.AreNotEqual(headers["Session-Id"], next["Session-Id"]);
        Assert.AreEqual(2, config.Count);
        var body = ClientProfiles.TestBody("codex", "test{{SESSION_ID}}", headers);
        Assert.AreEqual("test{{SESSION_ID}}", body["model"]!.ToString());
        Assert.IsTrue(body["stream"]!.GetValue<bool>());
        Assert.IsFalse(body["store"]!.GetValue<bool>());
        Assert.IsNull(body["max_output_tokens"]);
        Assert.AreEqual(7, body["input"]!.AsArray().Count);
        Assert.IsTrue(body["input"]!.ToJsonString().Contains("additional_tools", StringComparison.Ordinal));
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
}
