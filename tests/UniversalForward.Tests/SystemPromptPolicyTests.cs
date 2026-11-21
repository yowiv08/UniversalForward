using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

[TestClass]
public sealed class SystemPromptPolicyTests
{
    private static readonly string[] Models = ["model"];
    private const string ConfiguredText = "  请使用中文回答。\n保留格式。  ";
    private const string ResponseJson = """{"id":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""";
    private const string ResponsesDone = "{\"type\":\"response.completed\",\"response\":" + ResponseJson + "}";
    private const string MessagesSse = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"message\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"usage\":{\"input_tokens\":1}}}\n\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
        + "data: {\"type\":\"message_stop\"}\n\n";
    private static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();
    private static SystemPromptPolicy Policy(string mode = "append", string text = ConfiguredText) => new() { Mode = mode, Text = text };
    private static Account Account(string mode = "append", bool profile = false, string protocol = "responses", int retries = 0)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Name = "Key", Secret = "test-key" }], retries);
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["systemPromptPolicy"] = JsonSerializer.SerializeToNode(Policy(mode), RequestLogStore.Json);
            if (profile) settings["headerOverride"] = protocol == "responses" ? new JsonObject { ["Originator"] = "codex_exec" }
                : new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" };
        });
        return account;
    }

    [TestMethod]
    [DataRow("/v1/responses")]
    [DataRow("/v1/messages")]
    [DataRow("/v1/chat/completions")]
    public void DefaultAndOffRulesKeepEveryFieldUntouched(string endpoint)
    {
        var body = Object("""{"instructions":{"not":"a string"},"system":42,"input":[{"role":"system","content":"keep"}],"messages":[{"role":"user","content":"hello"}],"tools":[{"type":"function","name":"tool"}]}""");
        var before = body.ToJsonString();
        Assert.IsNull(new SystemPromptPolicy().Apply(body, endpoint));
        Assert.IsNull(Policy("off").Apply(body, endpoint));
        Assert.AreEqual(before, body.ToJsonString());
        Assert.IsNull(Policy().Apply(body, "/v1/models"));
        Assert.AreEqual(before, body.ToJsonString());
    }

    [TestMethod]
    [DataRow("/v1/responses", """{"instructions":"客户端\r\n\r\n渠道规则\r\n其他指令"}""")]
    [DataRow("/v1/responses", """{"instructions":"","input":[{"role":"user","content":"hello"},{"type":"message","role":"system","content":[{"type":"input_text","text":"渠道规则"}]}]}""")]
    [DataRow("/v1/responses", """{"input":[{"role":"developer","content":"渠道规则"}]}""")]
    [DataRow("/v1/messages", """{"system":"渠道规则"}""")]
    [DataRow("/v1/messages", """{"system":[{"type":"text","text":"渠道规则","cache_control":{"type":"ephemeral","ttl":"1h"}},{"type":"text","text":"其他指令"}]}""")]
    [DataRow("/v1/chat/completions", """{"messages":[{"role":"user","content":"hello"},{"role":"developer","content":[{"type":"text","text":"渠道规则","cache_control":{"type":"ephemeral"}}]}]}""")]
    public void AppendKeepsAnExistingInstructionInPlace(string endpoint, string json)
    {
        var body = Object(json);
        var before = body.ToJsonString();
        Assert.IsNull(Policy(text: "渠道规则").Apply(body, endpoint));
        Assert.AreEqual(before, body.ToJsonString());
    }

    [TestMethod]
    [DataRow("渠道规则\r\n", "前文\r\n渠道规则\r\n后文")]
    [DataRow("\n渠道规则", "前文\n渠道规则\n后文")]
    public void PromptOwnLineBreaksCanSeparateItFromAdjacentInstructions(string text, string instructions)
    {
        var body = new JsonObject { ["instructions"] = instructions };
        Assert.IsNull(Policy(text: text).Apply(body, "/v1/responses"));
        Assert.AreEqual(instructions, body["instructions"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("/v1/responses", """{"instructions":"不要回答中文","input":[{"role":"user","content":"回答中文"},{"type":"reasoning","role":"developer","content":"回答中文"},{"type":"function_call_output","role":"system","output":"回答中文"},{"role":"system","tools":[{"name":"t"}],"content":"回答中文"},{"role":"developer","content":[{"type":"output_text","text":"回答中文"}]}]}""")]
    [DataRow("/v1/messages", """{"system":[{"type":"text","text":"回答中文时请简短"},{"type":"vendor","text":"回答中文"}],"messages":[{"role":"user","content":"回答中文"}],"metadata":{"system":"回答中文"}}""")]
    [DataRow("/v1/chat/completions", """{"messages":[{"role":"system","content":"不要回答中文"},{"role":"user","content":"回答中文"},{"role":"assistant","content":"回答中文"},{"role":"tool","content":"回答中文"},{"role":"developer","tools":{"name":"t"},"content":"回答中文"},{"role":"developer","content":[{"type":"input_text","text":"回答中文"}]}]}""")]
    public void AppendIgnoresSubstringsAndTextOutsideInstructionFields(string endpoint, string json)
    {
        var body = Object(json);
        var policy = Policy(text: "回答中文");
        Assert.IsNotNull(policy.Apply(body, endpoint));
        var written = endpoint switch
        {
            "/v1/responses" => body["instructions"],
            "/v1/messages" => body["system"]!.AsArray()[^1]!["text"],
            _ => body["messages"]![0]!["content"]
        };
        Assert.AreEqual(endpoint == "/v1/messages" ? "回答中文" : "不要回答中文\n\n回答中文", written!.GetValue<string>());
        var after = body.ToJsonString();
        Assert.IsNull(policy.Apply(body, endpoint));
        Assert.AreEqual(after, body.ToJsonString());
    }

    [TestMethod]
    [DataRow("/v1/responses", """{"instructions":"渠道规则","input":[{"role":"system","content":"冲突指令"},{"role":"user","content":"问题"}]}""")]
    [DataRow("/v1/messages", """{"system":[{"type":"text","text":"渠道规则"},{"type":"text","text":"冲突指令"}],"messages":[{"role":"user","content":"问题"}]}""")]
    [DataRow("/v1/chat/completions", """{"messages":[{"role":"developer","content":"渠道规则"},{"role":"user","content":"问题"},{"role":"system","content":"冲突指令"}]}""")]
    public void ReplaceStillRemovesCompetingInstructionsWhenChannelTextIsPresent(string endpoint, string json)
    {
        var body = Object(json);
        var policy = Policy("replace", "渠道规则");
        Assert.IsNotNull(policy.Apply(body, endpoint));
        var expected = Object(endpoint switch
        {
            "/v1/responses" => """{"instructions":"渠道规则","input":[{"role":"user","content":"问题"}]}""",
            "/v1/messages" => """{"system":[{"type":"text","text":"渠道规则"}],"messages":[{"role":"user","content":"问题"}]}""",
            _ => """{"messages":[{"role":"developer","content":"渠道规则"},{"role":"user","content":"问题"}]}"""
        });
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.IsNull(policy.Apply(body, endpoint));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MessagesReplaceRetainsMatchedBlocksAndTheirCacheMetadata(bool claudeProfile)
    {
        var body = Object("""{"system":[{"type":"text","text":"旧指令"},{"type":"text","text":"渠道规则","cache_control":{"type":"ephemeral","ttl":"1h"}},{"type":"text","text":"渠道规则"},{"type":"text","text":"You are a Claude agent, built on Anthropic's Claude Agent SDK.","cache_control":{"type":"ephemeral"}}]}""");
        var original = body["system"]!.AsArray();
        var expected = new JsonArray();
        if (claudeProfile) expected.Add(original[3]!.DeepClone());
        expected.Add(original[1]!.DeepClone());
        var policy = Policy("replace", "渠道规则");
        Assert.IsNotNull(policy.Apply(body, "/v1/messages", preserveClaudeIdentity: claudeProfile));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body["system"]));
        Assert.IsNull(policy.Apply(body, "/v1/messages", preserveClaudeIdentity: claudeProfile));
    }

    [TestMethod]
    public void ChatReplaceKeepsExistingPromptBlockWhenRemovingConflictsAndMovingItToTheFront()
    {
        var body = Object("""{"messages":[{"role":"user","content":"第一问"},{"role":"system","tools":[{"name":"t"}],"content":"渠道规则"},{"role":"developer","name":"app","content":[{"type":"text","text":"旧指令"},{"type":"text","text":"渠道规则","cache_control":{"type":"ephemeral"}},{"type":"text","text":"渠道规则"}]},{"role":"system","content":"其他指令"},{"role":"assistant","content":"回答"}]}""");
        var expected = Object("""{"messages":[{"role":"system","name":"app","content":[{"type":"text","text":"渠道规则","cache_control":{"type":"ephemeral"}}]},{"role":"user","content":"第一问"},{"role":"system","tools":[{"name":"t"}],"content":"渠道规则"},{"role":"assistant","content":"回答"}]}""");
        var policy = Policy("replace", "渠道规则");
        Assert.IsNotNull(policy.Apply(body, "/v1/chat/completions"));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.IsNull(policy.Apply(body, "/v1/chat/completions"));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"instructions\":null}")]
    [DataRow("{\"instructions\":\"\"}")]
    public void ResponsesAppendSetsMissingInstructionsWithoutChangingHistory(string json)
    {
        var body = Object(json); body["input"] = new JsonArray(new JsonObject { ["role"] = "developer", ["content"] = "keep" });
        var input = body["input"]!.DeepClone();
        var decision = Policy().Apply(body, "/v1/responses")!;
        Assert.AreEqual(ConfiguredText, body["instructions"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(input, body["input"]));
        Assert.AreEqual("instructions", decision.Field);
        Assert.AreEqual("top_level", decision.Placement);
        Assert.AreEqual(0, decision.RemovedMessages);
        Assert.AreEqual(0, decision.DemotedMessages);
    }

    [TestMethod]
    public void ResponsesAppendPreservesExactWhitespaceHistoryAndNestedInstructions()
    {
        var body = Object("""{"instructions":"  old\nline  ","input":[{"role":"system","content":"original"},{"role":"developer","content":"development"},{"type":"function_call_output","call_id":"call","output":"result"}],"tools":[{"type":"function","name":"tool","parameters":{"properties":{"instructions":{"type":"string"}}}}],"metadata":{"instructions":"nested"},"previous_response_id":"previous"}""");
        var expected = body.DeepClone(); expected["instructions"] = "  old\nline  \n\n" + ConfiguredText;
        var decision = Policy().Apply(body, "/v1/responses")!;
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.AreEqual(ConfiguredText.Length, decision.TextLength);
        Assert.AreEqual(0, decision.DemotedMessages);
    }

    [TestMethod]
    public void ResponsesReplaceRemovesOnlyDirectSystemOrDeveloperMessages()
    {
        var body = Object("""{"instructions":{"bad":"old"},"input":[{"role":"system","content":"remove"},{"type":"message","role":"developer","content":"remove"},{"type":"message","role":"user","content":[{"type":"input_text","text":"question"}]},{"type":"message","role":"assistant","content":"answer","phase":"final_answer"},{"type":"function_call","role":"system","call_id":"call","arguments":"{}"},{"type":"function_call_output","call_id":"call","output":{"role":"system","content":"nested"}},{"type":"reasoning","role":"developer","encrypted_content":"keep"},{"type":null,"role":"system","content":"unrecognized"},{"role":"other","content":"keep"}],"tools":[{"name":"tool","description":"instructions"}],"metadata":{"system":"nested"},"system":"other protocol"}""");
        body["input"]!.AsArray().Add(new JsonObject { ["role"] = "system", ["content"] = "dynamic tools", ["tools"] = new JsonArray(new JsonObject { ["name"] = "keep" }) });
        body["input"]!.AsArray().Add(new JsonObject { ["type"] = "message", ["role"] = "developer", ["content"] = "dynamic tools", ["tools"] = new JsonObject { ["name"] = "keep" } });
        var expected = body.DeepClone();
        expected["instructions"] = ConfiguredText;
        expected["input"]!.AsArray().RemoveAt(1); expected["input"]!.AsArray().RemoveAt(0);
        var decision = Policy("replace").Apply(body, "/v1/responses")!;
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.AreEqual(2, decision.RemovedMessages);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"input\":null}")]
    [DataRow("{\"input\":[]}")]
    [DataRow("{\"previous_response_id\":\"resp_prev\"}")]
    public void ResponsesReplaceDoesNotInventInput(string json)
    {
        var body = Object(json);
        var expected = body.DeepClone(); expected["instructions"] = ConfiguredText;
        Policy("replace").Apply(body, "/v1/responses");
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    [DataRow("append")]
    [DataRow("replace")]
    public void ResponsesNativeInstructionsPreserveToolsReasoningAndRemoteHistory(string mode)
    {
        const string text = "  {api_key} {{SESSION_ID}}\r\n  插件规则 😀  ";
        var body = Object("""
            {"instructions":"old instructions","previous_response_id":"resp_prev","input":[
              {"type":"item_reference","id":"msg_keep"},{"type":"reasoning","id":"rs_keep","encrypted_content":"opaque"},
              {"role":"user","content":[{"type":"input_text","text":"question"},{"type":"input_image","image_url":"data:image/png;base64,AA=="}]},
              {"role":"assistant","content":"answer","phase":"final_answer"},
              {"type":"function_call","call_id":"call","name":"lookup","arguments":"{\"role\":\"system\"}"},
              {"type":"function_call_output","call_id":"call","output":"result"},
              {"type":"additional_tools","id":"tools","role":"system","tools":[{"type":"function","name":"dynamic"}]},
              {"role":"system","content":"late system","tools":[{"name":"message-tools"}]},
              {"type":"message","role":"system","content":[{"type":"input_text","text":"late override"}],"id":"system-id","custom":1},
              {"type":"message","role":"developer","content":"late developer"}
            ],"tools":[{"type":"function","name":"lookup","parameters":{"properties":{"instructions":{"type":"string"}}}}]}
            """);
        var expected = body.DeepClone();
        expected["instructions"] = mode == "append" ? "old instructions\n\n" + text : text;
        if (mode == "replace")
        {
            var input = expected["input"]!.AsArray();
            input.RemoveAt(input.Count - 1); input.RemoveAt(input.Count - 1);
        }

        var decision = Policy(mode, text).Apply(body, "/v1/responses")!;

        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.AreEqual(0, decision.DemotedMessages);
        Assert.AreEqual(mode == "replace" ? 2 : 0, decision.RemovedMessages);
        Assert.AreEqual(text.Length, decision.TextLength);
    }

    [TestMethod]
    [DataRow("append", "{}")]
    [DataRow("replace", "42")]
    [DataRow("append", "false")]
    public void InvalidResponsesInputDoesNotPartiallyRewriteInstructionsOrRoles(string mode, string value)
    {
        var body = new JsonObject { ["instructions"] = "keep", ["input"] = JsonNode.Parse(value) };
        var before = body.ToJsonString();
        Assert.ThrowsExactly<FormatException>(() => Policy(mode).Apply(body, "/v1/responses"));
        Assert.AreEqual(before, body.ToJsonString());
    }

    [TestMethod]
    [DataRow("append")]
    [DataRow("replace")]
    public void ResponsesKeepsStringInputSeparateFromInstructions(string mode)
    {
        const string question = "  question\n {{SESSION_ID}}  ";
        var body = new JsonObject { ["instructions"] = "old", ["input"] = question };
        Policy(mode).Apply(body, "/v1/responses");
        Assert.AreEqual(question, body["input"]!.GetValue<string>());
        Assert.AreEqual(mode == "append" ? "old\n\n" + ConfiguredText : ConfiguredText, body["instructions"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"system\":null}")]
    [DataRow("{\"system\":\"\"}")]
    public void MessagesAppendProvidesMissingSystem(string json)
    {
        var body = Object(json);
        Policy().Apply(body, "/v1/messages");
        Assert.AreEqual(1, body["system"]!.AsArray().Count);
        Assert.AreEqual(ConfiguredText, body["system"]![0]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public void MessagesAppendToStringKeepsConversationAndFormatting()
    {
        var body = Object("""{"system":" Original \n ","messages":[{"role":"user","content":[{"type":"text","text":"hello"},{"type":"image","source":{"type":"base64","data":"abc"}}]}],"tools":[{"name":"tool"}],"thinking":{"type":"enabled","budget_tokens":2000},"instructions":"other protocol"}""");
        var expected = body.DeepClone(); expected["system"] = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = " Original \n " },
            new JsonObject { ["type"] = "text", ["text"] = ConfiguredText });
        var decision = Policy().Apply(body, "/v1/messages")!;
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.AreEqual("system", decision.Field);
    }

    [TestMethod]
    public void MessagesAppendPreservesBlockOrderAndCacheMetadata()
    {
        var body = Object("""{"system":[{"type":"text","text":"first","cache_control":{"type":"ephemeral","ttl":"1h"},"custom":1},{"type":"text","text":"second","custom":{"nested":"keep"}}],"messages":[{"role":"user","content":"question"}],"cache_control":{"type":"ephemeral"}}""");
        var expected = body.DeepClone();
        expected["system"]!.AsArray().Add(new JsonObject { ["type"] = "text", ["text"] = ConfiguredText });
        Policy().Apply(body, "/v1/messages");
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    [DataRow("{\"system\":\"old\",\"messages\":[{\"role\":\"user\",\"content\":\"keep\"}],\"tools\":[{\"name\":\"keep\"}]}")]
    [DataRow("{\"system\":[{\"type\":\"text\",\"text\":\"old\",\"cache_control\":{\"type\":\"ephemeral\"}}],\"messages\":[{\"role\":\"user\",\"content\":\"keep\"}]}")]
    [DataRow("{\"system\":42,\"metadata\":{\"system\":\"keep\"}}")]
    public void MessagesReplaceChangesOnlyTopLevelSystem(string json)
    {
        var body = Object(json); var expected = body.DeepClone();
        expected["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ConfiguredText });
        Policy("replace").Apply(body, "/v1/messages");
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    [DataRow("system", "append")]
    [DataRow("developer", "append")]
    [DataRow("developer", "replace")]
    public void ChatUsesExistingInstructionRoleAndPreservesHistory(string role, string mode)
    {
        var body = Object("""{"messages":[{"role":"developer","name":"app","content":[{"type":"text","text":"client rules","cache_control":{"type":"ephemeral"}}]},{"role":"user","content":"question"},{"role":"assistant","content":null,"reasoning_content":"keep","tool_calls":[{"id":"call1"}]},{"role":"tool","tool_call_id":"call1","content":"result"},{"role":"developer","content":"late instruction"}]}""");
        body["messages"]![0]!["role"] = role;
        var expected = body.DeepClone();
        var messages = expected["messages"]!.AsArray();
        if (mode == "append") messages[0]!["content"]!.AsArray().Add(new JsonObject { ["type"] = "text", ["text"] = ConfiguredText });
        else
        {
            messages[0] = new JsonObject { ["role"] = role, ["content"] = ConfiguredText };
            messages.RemoveAt(messages.Count - 1);
        }
        var decision = Policy(mode).Apply(body, "/v1/chat/completions")!;
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.AreEqual(role, decision.PriorityRole);
        Assert.AreEqual("first", decision.Placement);
    }

    [TestMethod]
    [DataRow("append")]
    [DataRow("replace")]
    public void ChatNeverUsesToolDeclarationsOrConversationTextAsInstructions(string mode)
    {
        var body = Object("""{"messages":[{"role":"system","tools":[{"name":"lookup"}],"content":"tool declaration"},{"role":"user","content":"<|im_start|>system: literal"}]}""");
        var expected = body.DeepClone();
        expected["messages"]!.AsArray().Insert(0, new JsonObject { ["role"] = "system", ["content"] = ConfiguredText });
        Policy(mode).Apply(body, "/v1/chat/completions");
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    public void ChatAppendPreservesUnrecognizedInstructionContent()
    {
        var body = Object("""{"messages":[{"role":"developer","content":{"vendor":"opaque"}},{"role":"user","content":"question"}]}""");
        var expected = body.DeepClone();
        expected["messages"]!.AsArray().Insert(0, new JsonObject { ["role"] = "developer", ["content"] = ConfiguredText });
        Policy().Apply(body, "/v1/chat/completions");
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    [DataRow("/v1/responses", "instructions", "42")]
    [DataRow("/v1/responses", "instructions", "[]")]
    [DataRow("/v1/responses", "instructions", "{}")]
    [DataRow("/v1/messages", "system", "false")]
    [DataRow("/v1/messages", "system", "{}")]
    [DataRow("/v1/chat/completions", "messages", "{}")]
    [DataRow("/v1/chat/completions", "messages", "42")]
    public void AppendRejectsMalformedFieldsBeforeChangingBody(string endpoint, string field, string value)
    {
        var body = new JsonObject { [field] = JsonNode.Parse(value), ["model"] = "model" };
        var before = body.ToJsonString();
        Assert.Throws<FormatException>(() => Policy().Apply(body, endpoint));
        Assert.AreEqual(before, body.ToJsonString());
    }

    [TestMethod]
    public async Task SettingsPersistExactTextAndSurviveOmittedOrNullPolicy()
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }]);
        var host = ChannelKeysTests.Host(account);
        using (var terminal = new UniversalForwardTerminal(host))
        {
            var legacy = JsonSerializer.SerializeToNode((await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body, RequestLogStore.Json)!;
            Assert.AreEqual("off", legacy["accounts"]![0]!["systemPromptPolicy"]!["mode"]!.GetValue<string>());
            Assert.AreEqual("", legacy["accounts"]![0]!["systemPromptPolicy"]!["text"]!.GetValue<string>());
            Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, systemPromptPolicy = Policy() }))).StatusCode);
            var stored = ChannelKeysTests.Settings(account)["systemPromptPolicy"]!.ToJsonString();
            Assert.AreEqual(ConfiguredText, ChannelKeysTests.Settings(account)["systemPromptPolicy"]!["text"]!.GetValue<string>());
            Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, weight = 45 }))).StatusCode);
            Assert.AreEqual(stored, ChannelKeysTests.Settings(account)["systemPromptPolicy"]!.ToJsonString());
            Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, systemPromptPolicy = (SystemPromptPolicy?)null }))).StatusCode);
            Assert.AreEqual(stored, ChannelKeysTests.Settings(account)["systemPromptPolicy"]!.ToJsonString());
        }
        using var restarted = new UniversalForwardTerminal(host);
        var listed = JsonSerializer.SerializeToNode((await restarted.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body, RequestLogStore.Json)!;
        Assert.AreEqual(ConfiguredText, listed["accounts"]![0]!["systemPromptPolicy"]!["text"]!.GetValue<string>());
        Assert.AreEqual(200, (await restarted.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, systemPromptPolicy = Policy("off") }))).StatusCode);
        Assert.AreEqual(ConfiguredText, ChannelKeysTests.Settings(account)["systemPromptPolicy"]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("{\"mode\":\"invalid\",\"text\":\"hello\"}")]
    [DataRow("{\"mode\":null,\"text\":\"hello\"}")]
    [DataRow("{\"mode\":\"append\",\"text\":\" \"}")]
    [DataRow("{\"mode\":\"replace\",\"text\":\"\"}")]
    [DataRow("{\"mode\":\"off\",\"text\":null}")]
    [DataRow("{\"mode\":\"append\",\"text\":42}")]
    public async Task InvalidPolicyNeverChangesStoredChannel(string json)
    {
        var account = Account(); var before = ChannelKeysTests.Settings(account).ToJsonString();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var result = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, systemPromptPolicy = Object(json) }));
        Assert.AreEqual(400, result.StatusCode);
        Assert.AreEqual(before, ChannelKeysTests.Settings(account).ToJsonString());
    }

    [TestMethod]
    [DataRow("responses", "append")]
    [DataRow("messages", "replace")]
    [DataRow("chat/completions", "append")]
    public async Task LongPromptIsSavedAndForwardedVerbatim(string protocol, string mode)
    {
        var account = Account(); using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var text = "  " + new string('文', 65_536) + "\n末尾 😀  ";
        var saved = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, systemPromptPolicy = Policy(mode, text) }));
        Assert.AreEqual(200, saved.StatusCode);
        JsonObject? sent = null;
        var client = Client(async (request, ct) =>
        {
            sent = Object(await request.Content!.ReadAsStringAsync(ct));
            return Reply(protocol, false);
        });
        var result = await terminal.InvokeAsync(Context(account, client.Object, History(), protocol));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.IsNotNull(sent);
        var prompt = protocol switch
        {
            "responses" => sent["instructions"],
            "messages" => sent["system"]!.AsArray()[^1]!["text"],
            _ => sent["messages"]![0]!["content"]
        };
        Assert.AreEqual(protocol == "responses" && mode == "append" ? "old responses\n\n" + text : text, prompt!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("responses", "append", false, false)]
    [DataRow("responses", "replace", false, false)]
    [DataRow("responses", "append", true, false)]
    [DataRow("responses", "replace", true, false)]
    [DataRow("responses", "append", false, true)]
    [DataRow("responses", "replace", false, true)]
    [DataRow("messages", "append", false, false)]
    [DataRow("messages", "replace", false, false)]
    [DataRow("messages", "append", true, false)]
    [DataRow("messages", "replace", true, false)]
    [DataRow("messages", "append", false, true)]
    [DataRow("messages", "replace", false, true)]
    public async Task FinalPayloadAndRetriesUseOneRewriteAfterTemplates(string protocol, string mode, bool stream, bool profile)
    {
        var account = Account(mode, profile, protocol, retries: 1);
        var payloads = new List<string>();
        var client = Client(async (request, ct) =>
        {
            payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            return payloads.Count == 1 ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("retry") }
                : Reply(protocol, stream || profile);
        });
        using var store = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account)) { RequestLogs = store };
        var original = History(); original["stream"] = stream;
        var context = Context(account, client.Object, original, protocol, stream);
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        if (result.Response.RawStream is { } output) await foreach (var _ in output) { }
        Assert.AreEqual(2, payloads.Count); Assert.AreEqual(payloads[0], payloads[1]);
        var sent = Object(payloads[0]);
        AssertPrompt(sent, protocol, mode, profile);
        Assert.IsTrue(JsonNode.DeepEquals(original["tools"], sent["tools"]));
        Assert.IsTrue(JsonNode.DeepEquals(original["thinking"], sent["thinking"]));
        Assert.AreEqual(original.ToJsonString(), context.Request.OriginalBody!.Value.GetRawText());
        await store.FlushAsync();
        var row = JsonSerializer.SerializeToNode(store.List(new Dictionary<string, string>()), RequestLogStore.Json)!["rows"]![0]!;
        Assert.AreEqual(mode, row["systemPromptRewrite"]!["mode"]!.GetValue<string>());
        Assert.AreEqual(protocol == "responses" ? "instructions" : "system", row["systemPromptRewrite"]!["field"]!.GetValue<string>());
        Assert.AreEqual(ConfiguredText.Length, row["systemPromptRewrite"]!["textLength"]!.GetValue<int>());
        Assert.IsNull(row["systemPromptRewrite"]!["text"]);
        Assert.AreEqual(protocol == "responses" ? "system_or_developer" : "system", row["systemPromptRewrite"]!["priorityRole"]!.GetValue<string>());
        Assert.AreEqual(protocol == "responses" ? "top_level" : "last", row["systemPromptRewrite"]!["placement"]!.GetValue<string>());
        Assert.AreEqual(0, row["systemPromptRewrite"]!["demotedMessages"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow("responses", "append", false)]
    [DataRow("responses", "append", true)]
    [DataRow("responses", "replace", false)]
    [DataRow("responses", "replace", true)]
    [DataRow("chat/completions", "append", false)]
    [DataRow("chat/completions", "replace", false)]
    [DataRow("messages", "append", false)]
    [DataRow("messages", "append", true)]
    [DataRow("messages", "replace", false)]
    [DataRow("messages", "replace", true)]
    public async Task ReplayedThenOverwrittenThenMissingInstructionsAreCheckedEachTurn(string protocol, string mode, bool profile)
    {
        var account = Account(mode, profile, protocol);
        using var store = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account)) { RequestLogs = store };
        var sent = new List<JsonObject>();
        var client = Client(async (request, ct) =>
        {
            sent.Add(Object(await request.Content!.ReadAsStringAsync(ct)));
            return Reply(protocol, profile);
        });
        var field = protocol switch { "responses" => "instructions", "messages" => "system", _ => "messages" };
        JsonObject Incoming(string? instructions)
        {
            var body = protocol == "responses"
                ? Object("""{"model":"model","previous_response_id":"resp_prev","input":[{"type":"item_reference","id":"msg_old"}]}""")
                : Object("""{"model":"model","messages":[{"role":"user","content":"question"}]}""");
            if (instructions is not null)
            {
                if (field == "messages") body[field]!.AsArray().Insert(0, new JsonObject { ["role"] = "developer", ["content"] = instructions });
                else body[field] = instructions;
            }
            return body;
        }
        async Task<JsonObject> Forward(JsonObject body)
        {
            var ctx = Context(account, client.Object, body, protocol);
            ctx.Request.DownstreamRequestHeaders["Session-Id"] = ["same-session"];
            ctx.Request.DownstreamRequestHeaders["x-claude-code-session-id"] = ["same-session"];
            Assert.AreEqual(200, (await terminal.InvokeAsync(ctx)).Response.StatusCode);
            return sent[^1];
        }

        var first = await Forward(Incoming("client"));
        var echoed = await Forward(first);
        Assert.IsTrue(JsonNode.DeepEquals(first[field], echoed[field]));

        var changed = await Forward(Incoming("new rules"));
        var missing = await Forward(Incoming(null));
        if (protocol == "messages")
        {
            var blocks = changed[field]!.AsArray();
            Assert.AreEqual(ConfiguredText, blocks[^1]!["text"]!.GetValue<string>());
            if (mode == "append") Assert.AreEqual("new rules", blocks[^2]!["text"]!.GetValue<string>());
            else Assert.AreEqual(profile ? 2 : 1, blocks.Count);
            Assert.AreEqual(profile ? 2 : 1, missing[field]!.AsArray().Count);
            Assert.AreEqual(ConfiguredText, missing[field]!.AsArray()[^1]!["text"]!.GetValue<string>());
        }
        else
        {
            Assert.AreEqual(mode == "replace" ? ConfiguredText : "new rules\n\n" + ConfiguredText,
                (protocol == "responses" ? changed[field] : changed[field]![0]!["content"])!.GetValue<string>());
            Assert.AreEqual(ConfiguredText, (protocol == "responses" ? missing[field] : missing[field]![0]!["content"])!.GetValue<string>());
            if (protocol == "responses") Assert.IsTrue(JsonNode.DeepEquals(Incoming(null)["input"], missing["input"]));
        }
        await store.FlushAsync();
        var rows = JsonSerializer.SerializeToNode(store.List(new Dictionary<string, string>()), RequestLogStore.Json)!["rows"]!.AsArray();
        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual(3, rows.Count(row => row!["systemPromptRewrite"] is not null));
    }

    [TestMethod]
    [DataRow("responses", "append", false)]
    [DataRow("responses", "replace", true)]
    [DataRow("messages", "append", false)]
    [DataRow("messages", "replace", true)]
    public async Task RewritingFollowsActualSelectedUpstreamEndpoint(string protocol, string mode, bool profile)
    {
        var account = Account(mode, profile, protocol);
        ReasoningPolicyTests.Edit(account, settings => settings["modelProtocols"] = JsonSerializer.SerializeToNode(
            new Dictionary<string, ModelProtocolOptions> { ["model"] = new() { Protocols = [protocol], PreferredProtocol = protocol } }, RequestLogStore.Json));
        JsonObject? sent = null;
        var client = Client(async (request, ct) =>
        {
            Assert.AreEqual("/v1/" + protocol, request.RequestUri!.AbsolutePath);
            sent = Object(await request.Content!.ReadAsStringAsync(ct)); return Reply(protocol, profile);
        });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var incoming = protocol == "responses" ? "messages" : "responses";
        var result = await terminal.InvokeAsync(Context(account, client.Object, History(), incoming));
        Assert.AreEqual(200, result.Response.StatusCode);
        AssertPrompt(sent!, protocol, mode, profile);
        Assert.AreEqual("old " + incoming, sent![incoming == "responses" ? "instructions" : "system"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("responses")]
    [DataRow("messages")]
    public async Task InvalidAppendFieldReturns400WithoutSendingOrHostReplay(string protocol)
    {
        var account = Account(protocol: protocol, retries: 2);
        var original = History(); original[protocol == "responses" ? "instructions" : "system"] = new JsonObject { ["invalid"] = true };
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var result = await terminal.InvokeAsync(Context(account, client.Object, original, protocol));
        Assert.AreEqual(400, result.Response.StatusCode);
        Assert.AreEqual(PluginRetryAction.None, result.Attempt.Decision!.Retry);
        Assert.AreEqual(0, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("responses", "append", false)]
    [DataRow("responses", "replace", false)]
    [DataRow("responses", "append", true)]
    [DataRow("responses", "replace", true)]
    [DataRow("messages", "append", false)]
    [DataRow("messages", "replace", false)]
    [DataRow("messages", "append", true)]
    [DataRow("messages", "replace", true)]
    public async Task ConnectionTestsApplyRewriteToFinalGeneratedBody(string protocol, string mode, bool profile)
    {
        var account = Account(mode, profile, protocol); var host = ChannelKeysTests.Host(account);
        JsonObject? sent = null;
        using var handler = new RequestHandler(async request => { sent = Object(await request.Content!.ReadAsStringAsync()); return Reply(protocol, profile); });
        Mock.Get(host.Services.Http).Setup(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        PluginJobRegistration? registration = null; var builder = new Mock<IPluginBuilder>();
        builder.Setup(value => value.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(job => registration = job);
        terminal.Configure(builder.Object);
        var result = await registration!.ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = account.Id, models = Models, endpoint = "/v1/" + protocol }), _ => { }, CancellationToken.None));
        Assert.IsTrue(ConnectionTestAssertions.Rows(terminal)[0].GetProperty("success").GetBoolean(), result.ToString());
        var field = protocol == "responses" ? "instructions" : "system";
        if (protocol == "messages")
        {
            var blocks = sent![field]!.AsArray();
            Assert.AreEqual(ConfiguredText, blocks[^1]!["text"]!.GetValue<string>());
            Assert.AreEqual(profile ? mode == "append" ? 3 : 2 : 1, blocks.Count);
            if (profile) StringAssert.Contains(blocks[0]!["text"]!.GetValue<string>(), "Claude agent");
        }
        else
        {
            Assert.AreEqual(profile && mode == "append" ? "Reply only OK. Do not call tools.\n\n" + ConfiguredText : ConfiguredText,
                sent!["instructions"]!.GetValue<string>());
            if (profile)
            {
                var input = sent["input"]!.AsArray();
                Assert.AreEqual(1, input.Count);
                Assert.AreEqual("user", input[0]!["role"]!.GetValue<string>());
            }
            else Assert.AreEqual("Reply with OK.", sent["input"]!.GetValue<string>());
        }
    }

    [TestMethod]
    [DataRow("append", false, false)]
    [DataRow("replace", false, false)]
    [DataRow("append", true, true)]
    [DataRow("replace", true, true)]
    public async Task RealWebSocketSendsSameRewrittenPayloadAcrossRetries(string mode, bool profile, bool stream)
    {
        var payloads = new List<string>();
        await using var server = new WebSocketLoopback(async (peer, number, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            payloads.Add(await WebSocketLoopback.ReceiveAsync(socket, ct));
            if (number == 0) await WebSocketLoopback.SendAsync(socket, """{"type":"error","status":429,"error":{"type":"rate_limit_error","code":"rate_limit_exceeded","message":"retry"}}""", ct);
            else await WebSocketLoopback.SendAsync(socket, ResponsesDone, ct);
        }, connections: 2);
        var account = Account(mode, profile, retries: 1);
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["baseUrl"] = server.Address.ToString().TrimEnd('/'); settings["responsesTransport"] = "websocket";
            settings["requestPolicy"]!["rateLimitRetryEnabled"] = true;
            settings["requestPolicy"]!["responseMaxRetries"] = 1;
            settings["requestPolicy"]!["responseRetryIntervalSeconds"] = 1;
        });
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http).Setup(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => server.CreateClient());
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.InvokeAsync(Context(account, client.Object, History(), stream: stream));
        Assert.AreEqual(200, result.Response.StatusCode, Encoding.UTF8.GetString(result.Response.RawContent ?? []));
        if (result.Response.RawStream is { } output) await foreach (var _ in output) { }
        await server.Completion;
        Assert.AreEqual(2, payloads.Count); Assert.AreEqual(payloads[0], payloads[1]);
        var sent = Object(payloads[0]); Assert.AreEqual("response.create", sent["type"]!.GetValue<string>());
        Assert.IsNull(sent["stream"]); AssertPrompt(sent, "responses", mode, profile);
        Assert.AreEqual(0, client.Invocations.Count);
    }

    private static JsonObject History() => Object("""{"model":"model","instructions":"old responses","system":"old messages","input":[{"role":"system","content":"system input"},{"type":"message","role":"developer","content":"developer input"},{"role":"user","content":"question"},{"type":"function_call_output","call_id":"call","output":"result"}],"messages":[{"role":"user","content":"question"}],"tools":[{"type":"function","name":"tool","parameters":{"type":"object"}}],"thinking":{"type":"enabled","budget_tokens":2048},"max_tokens":4096}""");
    private static void AssertPrompt(JsonObject sent, string protocol, string mode, bool profile)
    {
        if (protocol == "responses")
        {
            Assert.AreEqual(mode == "append" ? "old responses\n\n" + ConfiguredText : ConfiguredText, sent["instructions"]!.GetValue<string>());
            var expectedInput = History()["input"]!.AsArray();
            if (mode == "replace") { expectedInput.RemoveAt(1); expectedInput.RemoveAt(0); }
            Assert.IsTrue(JsonNode.DeepEquals(expectedInput, sent["input"]));
        }
        else if (profile)
        {
            var blocks = sent["system"]!.AsArray();
            Assert.AreEqual(ConfiguredText, blocks[^1]!["text"]!.GetValue<string>());
            Assert.AreEqual(mode == "append" ? 3 : 2, blocks.Count);
            StringAssert.Contains(blocks[0]!["text"]!.GetValue<string>(), "Claude agent");
            if (mode == "append") Assert.AreEqual("old messages", blocks[1]!["text"]!.GetValue<string>());
        }
        else
        {
            var blocks = sent["system"]!.AsArray();
            Assert.AreEqual(ConfiguredText, blocks[^1]!["text"]!.GetValue<string>());
            Assert.AreEqual(mode == "append" ? 2 : 1, blocks.Count);
            if (mode == "append") Assert.AreEqual("old messages", blocks[0]!["text"]!.GetValue<string>());
        }
    }
    private static PluginAttemptContext Context(Account account, IPluginHttpClient client, JsonObject body, string protocol = "responses", bool stream = false)
    {
        var context = ChannelKeysTests.Attempt(account, client);
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(body);
        context.Request.Endpoint = "/v1/" + protocol; context.Request.Stream = stream;
        return context;
    }
    private static Mock<IPluginHttpClient> Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
    {
        var client = new Mock<IPluginHttpClient>();
        client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) => reply(request, ct));
        return client;
    }
    private static HttpResponseMessage Reply(string protocol, bool stream) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(stream ? protocol == "responses" ? "data: " + ResponsesDone + "\n\n" : MessagesSse
            : protocol switch
            {
                "responses" => ResponseJson,
                "chat/completions" => """{"choices":[{"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""",
                _ => """{"id":"message","type":"message","role":"assistant","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""
            },
            Encoding.UTF8, stream ? "text/event-stream" : "application/json")
    };
    private sealed class RequestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }
}
