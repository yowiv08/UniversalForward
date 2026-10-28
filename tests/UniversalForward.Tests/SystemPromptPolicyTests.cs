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
    [DataRow("{}")]
    [DataRow("{\"instructions\":null}")]
    [DataRow("{\"instructions\":\"\"}")]
    public void ResponsesAppendProvidesMissingInstructionsWithoutChangingInput(string json)
    {
        var body = Object(json); body["input"] = new JsonArray(new JsonObject { ["role"] = "developer", ["content"] = "keep" });
        var input = body["input"]!.DeepClone();
        var decision = Policy().Apply(body, "/v1/responses")!;
        Assert.AreEqual(ConfiguredText, body["instructions"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(input, body["input"]));
        Assert.AreEqual("instructions", decision.Field);
        Assert.AreEqual(0, decision.RemovedMessages);
    }

    [TestMethod]
    public void ResponsesAppendPreservesExactWhitespaceHistoryAndNestedInstructions()
    {
        var body = Object("""{"instructions":"  old\nline  ","input":[{"role":"system","content":"original"},{"role":"developer","content":"development"},{"type":"function_call_output","call_id":"call","output":"result"}],"tools":[{"type":"function","name":"tool","parameters":{"properties":{"instructions":{"type":"string"}}}}],"metadata":{"instructions":"nested"},"previous_response_id":"previous"}""");
        var expected = body.DeepClone(); expected["instructions"] = "  old\nline  \n\n" + ConfiguredText;
        var decision = Policy().Apply(body, "/v1/responses")!;
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.AreEqual(ConfiguredText.Length, decision.TextLength);
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
    [DataRow("question")]
    [DataRow(null)]
    public void ResponsesReplacePreservesStringOrMissingInput(string? input)
    {
        var body = new JsonObject { ["instructions"] = "old" };
        if (input is not null) body["input"] = input;
        Policy("replace").Apply(body, "/v1/responses");
        Assert.AreEqual(input, body["input"]?.GetValue<string>());
        Assert.AreEqual(ConfiguredText, body["instructions"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"system\":null}")]
    [DataRow("{\"system\":\"\"}")]
    public void MessagesAppendProvidesMissingSystem(string json)
    {
        var body = Object(json);
        Policy().Apply(body, "/v1/messages");
        Assert.AreEqual(ConfiguredText, body["system"]!.GetValue<string>());
    }

    [TestMethod]
    public void MessagesAppendToStringKeepsConversationAndFormatting()
    {
        var body = Object("""{"system":" Original \n ","messages":[{"role":"user","content":[{"type":"text","text":"hello"},{"type":"image","source":{"type":"base64","data":"abc"}}]}],"tools":[{"name":"tool"}],"thinking":{"type":"enabled","budget_tokens":2000},"instructions":"other protocol"}""");
        var expected = body.DeepClone(); expected["system"] = " Original \n \n\n" + ConfiguredText;
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
        var body = Object(json); var expected = body.DeepClone(); expected["system"] = ConfiguredText;
        Policy("replace").Apply(body, "/v1/messages");
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    [DataRow("/v1/responses", "instructions", "42")]
    [DataRow("/v1/responses", "instructions", "[]")]
    [DataRow("/v1/responses", "instructions", "{}")]
    [DataRow("/v1/messages", "system", "false")]
    [DataRow("/v1/messages", "system", "{}")]
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
    public async Task TextLengthUsesUtf16UnitsAndRejectsOversizeWithoutWriting()
    {
        var account = Account(); using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var text = string.Concat(Enumerable.Repeat("😀", 16000));
        Assert.AreEqual(32000, text.Length);
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, systemPromptPolicy = Policy(text: text) }))).StatusCode);
        var before = ChannelKeysTests.Settings(account).ToJsonString();
        Assert.AreEqual(400, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, systemPromptPolicy = Policy(text: text + "x") }))).StatusCode);
        Assert.AreEqual(before, ChannelKeysTests.Settings(account).ToJsonString());
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
        Assert.IsTrue(result!.Value.GetProperty("rows")[0].GetProperty("success").GetBoolean(), result.ToString());
        var field = protocol == "responses" ? "instructions" : "system";
        if (protocol == "messages" && profile)
        {
            var blocks = sent![field]!.AsArray();
            Assert.AreEqual(ConfiguredText, blocks[^1]!["text"]!.GetValue<string>());
            Assert.AreEqual(mode == "append" ? 3 : 2, blocks.Count);
            StringAssert.Contains(blocks[0]!["text"]!.GetValue<string>(), "Claude agent");
        }
        else if (mode == "replace" || !profile) Assert.AreEqual(ConfiguredText, sent![field]!.GetValue<string>());
        else Assert.AreEqual("Reply only OK. Do not call tools.\n\n" + ConfiguredText, sent![field]!.GetValue<string>());
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
            Assert.AreEqual(mode == "append" ? 4 : 2, sent["input"]!.AsArray().Count);
        }
        else if (profile)
        {
            var blocks = sent["system"]!.AsArray();
            Assert.AreEqual(ConfiguredText, blocks[^1]!["text"]!.GetValue<string>());
            Assert.AreEqual(mode == "append" ? 3 : 2, blocks.Count);
            StringAssert.Contains(blocks[0]!["text"]!.GetValue<string>(), "Claude agent");
            if (mode == "append") Assert.AreEqual("old messages", blocks[1]!["text"]!.GetValue<string>());
        }
        else Assert.AreEqual(mode == "append" ? "old messages\n\n" + ConfiguredText : ConfiguredText, sent["system"]!.GetValue<string>());
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
            : protocol == "responses" ? ResponseJson : """{"id":"message","type":"message","role":"assistant","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""",
            Encoding.UTF8, stream ? "text/event-stream" : "application/json")
    };
    private sealed class RequestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }
}
