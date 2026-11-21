using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ChatCompletionsTests
{
    private const string Endpoint = "/v1/chat/completions";
    private const string Completed = """{"id":"chatcmpl-test","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"OK","reasoning_content":"Thinking"},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":4,"total_tokens":13,"prompt_tokens_details":{"cached_tokens":3},"completion_tokens_details":{"reasoning_tokens":2}}}""";
    private const string Stream = "data: {\"id\":\"chatcmpl-test\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"Thinking\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4,\"total_tokens\":13,\"prompt_tokens_details\":{\"cached_tokens\":3},\"completion_tokens_details\":{\"reasoning_tokens\":2}}}\n\n"
        + "data: [DONE]\n\n";

    private static JsonObject History() => JsonNode.Parse("""
        {"model":"universalforward/model","messages":[
          {"role":"system","content":"System rules"},
          {"role":"developer","content":"Developer rules"},
          {"role":"user","content":[{"type":"text","text":"Read this"},{"type":"image_url","image_url":{"url":"https://example.test/image.png"}}]},
          {"role":"assistant","content":null,"reasoning_content":"Previous thinking","reasoning_details":[{"type":"reasoning.encrypted","data":"opaque"}],"tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},
          {"role":"tool","tool_call_id":"call_1","content":"Tool result"}],
         "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}],
         "tool_choice":"auto","reasoning_effort":"low","max_completion_tokens":4096,"stream_options":{"include_usage":true},
         "response_format":{"type":"json_object"},"custom_vendor":{"enabled":true}}
        """)!.AsObject();

    [TestMethod]
    [DataRow(false, "https://upstream.example")]
    [DataRow(true, "https://upstream.example/v1")]
    [DataRow(false, "https://upstream.example/gateway/v1/chat/completions/")]
    [DataRow(true, "https://upstream.example/gateway/v1/chat/completions/")]
    public async Task NativeForwardingPreservesHistoryResponseAndUsage(bool stream, string baseUrl)
    {
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }]);
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["baseUrl"] = baseUrl;
            settings["responsesTransport"] = "websocket";
            settings["stripResponsesResourceState"] = true;
            settings["headerOverrideMode"] = "perEndpoint";
            settings["endpointHeaderOverrides"] = new JsonObject
            {
                [Endpoint] = new JsonObject { ["useCommon"] = false,
                    ["headers"] = new JsonObject { ["X-Chat-Key"] = "{api_key}", ["X-Client-Trace"] = "{client_header:X-Trace}" } }
            };
        });
        var original = History(); original["stream"] = stream;
        var expected = original.DeepClone(); expected["model"] = "model";
        var response = stream ? Stream : Completed;
        var client = Client(async request =>
        {
            Assert.AreEqual((baseUrl.Contains("gateway", StringComparison.Ordinal) ? "/gateway" : "") + Endpoint,
                request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer channel-key", request.Headers.Authorization!.ToString());
            Assert.AreEqual("channel-key", request.Headers.GetValues("X-Chat-Key").Single());
            Assert.AreEqual("client-trace", request.Headers.GetValues("X-Client-Trace").Single());
            Assert.IsFalse(request.Headers.Contains("x-api-key"));
            Assert.IsFalse(request.Headers.Contains("X-Selected"));
            Assert.IsTrue(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync())));
            return Reply(response, stream);
        });
        using var logs = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account)) { RequestLogs = logs };
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.Endpoint = Endpoint;
        context.Request.Stream = stream;
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(original);
        context.Request.RequestHeaders["Authorization"] = "Bearer client-key";
        context.Request.RequestHeaders["x-api-key"] = "client-key";
        context.Request.RequestHeaders["X-Trace"] = "client-trace";
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(stream, result.Response.IsStreaming);
        if (stream)
        {
            using var output = new MemoryStream();
            await foreach (var bytes in result.Response.RawStream!) await output.WriteAsync(bytes);
            Assert.AreEqual(response, Encoding.UTF8.GetString(output.ToArray()));
        }
        else
        {
            Assert.AreEqual(response, Encoding.UTF8.GetString(result.Response.RawContent!));
            Assert.AreEqual(new Usage(9, 4, 13), result.Response.Usage);
        }
        if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
        Assert.IsTrue(JsonNode.DeepEquals(original, JsonNode.Parse(context.Request.OriginalBody!.Value.GetRawText())));
        Assert.AreEqual(1, client.Invocations.Count);
        await logs.FlushAsync();
        var row = JsonSerializer.SerializeToNode(logs.List(new Dictionary<string, string>()), RequestLogStore.Json)!["rows"]![0]!;
        Assert.AreEqual("low", row["receivedReasoning"]!["reasoning_effort"]!.ToString());
        Assert.AreEqual("low", row["sentReasoning"]!["reasoning_effort"]!.ToString());
        Assert.IsNull(row["reasoningMapping"]);
        Assert.AreEqual(9L, row["tokenUsage"]!["inputTokens"]!.GetValue<long>());
        Assert.AreEqual(4L, row["tokenUsage"]!["outputTokens"]!.GetValue<long>());
        Assert.AreEqual(3L, row["tokenUsage"]!["cacheReadTokens"]!.GetValue<long>());
    }

    [TestMethod]
    [DataRow("{\"reasoning_content\":\"Thinking\"}", false)]
    [DataRow("{\"reasoning_content\":\"Thinking\"}", true)]
    [DataRow("{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"data\":\"opaque\"}]}", false)]
    [DataRow("{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"data\":\"opaque\"}]}", true)]
    [DataRow("{\"audio\":{\"data\":\"aGVsbG8=\",\"transcript\":\"Hello\"}}", false)]
    [DataRow("{\"audio\":{\"data\":\"aGVsbG8=\",\"transcript\":\"Hello\"}}", true)]
    public async Task ReasoningOrAudioOnlyResponseIsOutputAndNeverRetriedAsEmpty(string output, bool stream)
    {
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }]);
        ReasoningPolicyTests.Edit(account, settings => settings["requestPolicy"] = JsonNode.Parse(
            """{"emptyResponseRetryEnabled":true,"responseMaxRetries":1,"responseRetryIntervalSeconds":1}"""));
        var choice = new JsonObject { ["index"] = 0, [stream ? "delta" : "message"] = JsonNode.Parse(output), ["finish_reason"] = "stop" };
        var json = new JsonObject { ["choices"] = new JsonArray(choice) }.ToJsonString();
        var response = stream ? "data: " + json + "\n\ndata: [DONE]\n\n" : json;
        var client = Client(_ => Task.FromResult(Reply(response, stream)));
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.Endpoint = Endpoint; context.Request.Stream = stream;
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        if (stream)
        {
            using var collected = new MemoryStream();
            await foreach (var bytes in result.Response.RawStream!) await collected.WriteAsync(bytes);
            Assert.AreEqual(response, Encoding.UTF8.GetString(collected.ToArray()));
        }
        else Assert.AreEqual(response, Encoding.UTF8.GetString(result.Response.RawContent!));
        if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task ResponsesStateErrorDoesNotStripOrRetryNativeChatHistory()
    {
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }]);
        ReasoningPolicyTests.Edit(account, settings => settings["stripResponsesResourceState"] = true);
        var original = History(); var expected = original.DeepClone(); expected["model"] = "model";
        var client = Client(async request =>
        {
            Assert.IsTrue(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync())));
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"code":"invalid_encrypted_content","message":"invalid encrypted content"}}""") };
        });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.Endpoint = Endpoint; context.Request.OriginalBody = JsonSerializer.SerializeToElement(original);
        Assert.AreEqual(400, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
        Assert.IsTrue(JsonNode.DeepEquals(original, JsonNode.Parse(context.Request.OriginalBody!.Value.GetRawText())));
    }

    [TestMethod]
    public void ChatConversationIdentityUsesMessageAndReasoningHistory()
    {
        var original = History(); original["input"] = "Unrelated Responses input";
        var fingerprint = ConversationFingerprint.Read(original, Endpoint);
        Assert.IsTrue(fingerprint.HasReply);
        var edited = original.DeepClone(); edited["input"] = "Different unrelated input";
        CollectionAssert.AreEqual(fingerprint.Prefixes.ToArray(), ConversationFingerprint.Read(edited, Endpoint).Prefixes.ToArray());
        edited["messages"]![3]!["reasoning_content"] = "Different thinking";
        Assert.AreNotEqual(fingerprint.Prefixes[^1], ConversationFingerprint.Read(edited, Endpoint).Prefixes[^1]);
    }

    [TestMethod]
    [DataRow("off")]
    [DataRow("append")]
    [DataRow("replace")]
    public async Task ReasoningAndPromptSettingsApplyOnceAcrossRetries(string mode)
    {
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }], retries: 1);
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["systemPromptPolicy"] = new JsonObject { ["mode"] = mode, ["text"] = "Channel rules" };
            settings["reasoningPolicy"] = JsonNode.Parse("""{"defaults":{"responses":{"mode":"fixed","fixedEffort":"wrong"}},"models":{"model":{"chat":{"mode":"map","mappings":[{"from":"low","to":"high"},{"from":"high","to":"none"}]}}}}""");
        });
        var original = History();
        // A dynamic tool declaration must survive replacement, including its system role.
        original["messages"]!.AsArray().Insert(2, JsonNode.Parse("""{"role":"system","content":"Tools","tools":[{"name":"lookup"}]}"""));
        var expected = original.DeepClone(); expected["model"] = "model"; expected["reasoning_effort"] = "high";
        var messages = expected["messages"]!.AsArray();
        if (mode == "replace") { messages.RemoveAt(1); messages[0]!["content"] = "Channel rules"; }
        if (mode == "append") messages[0]!["content"] = "System rules\n\nChannel rules";
        var bodies = new List<string>();
        var client = Client(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1 ? new(HttpStatusCode.BadGateway) { Content = new StringContent("retry") } : Reply(Completed);
        });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.Endpoint = Endpoint;
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(original);
        Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(2, bodies.Count);
        Assert.AreEqual(bodies[0], bodies[1]);
        Assert.IsTrue(JsonNode.DeepEquals(expected, JsonNode.Parse(bodies[1])));
        Assert.IsTrue(JsonNode.DeepEquals(original, JsonNode.Parse(context.Request.OriginalBody!.Value.GetRawText())));
    }

    [TestMethod]
    [DataRow("{}", "high")]
    [DataRow("{\"reasoning_effort\":null}", "high")]
    [DataRow("{\"reasoning_effort\":\" LOW \"}", "max")]
    [DataRow("{\"reasoning_effort\":\"\"}", null)]
    [DataRow("{\"reasoning_effort\":\"vendor\"}", null)]
    [DataRow("{\"reasoning_effort\":42}", null)]
    [DataRow("{\"reasoning_effort\":{}}", null)]
    public void ChatReasoningMappingUsesTopLevelEffort(string json, string? expected)
    {
        var policy = new ReasoningPolicy { Defaults = new() { ["chat"] = new()
        { Mode = "map", DefaultEffort = "high", Mappings = [new() { From = "low", To = "max" }] } } };
        policy.Validate();
        var original = JsonNode.Parse(json)!.AsObject();
        original["reasoning"] = new JsonObject { ["effort"] = "unrelated" };
        var decision = policy.Resolve(original, "model", Endpoint);
        Assert.AreEqual(expected, decision?.Sent);
        if (decision is not null)
        {
            var prepared = original.DeepClone().AsObject();
            ReasoningPolicy.Apply(prepared, decision);
            Assert.AreEqual(expected, prepared["reasoning_effort"]!.ToString());
            Assert.AreEqual("unrelated", prepared["reasoning"]!["effort"]!.ToString());
        }
        policy.Models["model"] = new() { ["chat"] = new() { Mode = "off" } };
        Assert.IsNull(policy.Resolve(original, "model", Endpoint));
    }

    [TestMethod]
    public async Task ChatConfigurationSurvivesSaveReloadAndControlsAdmission()
    {
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }]);
        var host = ChannelKeysTests.Host(account);
        var input = new JsonObject
        {
            ["id"] = account.Id, ["label"] = account.Label, ["endpoints"] = new JsonArray(Endpoint),
            ["modelProtocols"] = JsonNode.Parse("""{"model":{"protocols":["chat"],"preferredProtocol":"chat","nativeOnly":true}}"""),
            ["headerOverrideMode"] = "perEndpoint",
            ["endpointHeaderOverrides"] = new JsonObject { [Endpoint] = JsonNode.Parse("""{"useCommon":false,"headers":{"X-Protocol":"chat"}}""") },
            ["reasoningPolicy"] = JsonNode.Parse("""{"defaults":{"chat":{"mode":"fixed","fixedEffort":"high"}}}""")
        };
        using (var terminal = new UniversalForwardTerminal(host))
        {
            Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(input))).StatusCode);
            Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, weight = 50 }))).StatusCode);
        }
        using var reloaded = new UniversalForwardTerminal(host);
        var listed = JsonSerializer.SerializeToNode((await reloaded.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body, RequestLogStore.Json)!["accounts"]![0]!;
        foreach (var field in new[] { "endpoints", "modelProtocols", "headerOverrideMode", "endpointHeaderOverrides" })
            Assert.IsTrue(JsonNode.DeepEquals(input[field], listed[field]), field);
        var client = Client(async request =>
        {
            Assert.AreEqual(Endpoint, request.RequestUri!.AbsolutePath);
            Assert.AreEqual("chat", request.Headers.GetValues("X-Protocol").Single());
            Assert.AreEqual("high", JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["reasoning_effort"]!.ToString());
            return Reply(Completed);
        });
        var context = ChannelKeysTests.Attempt(account, client.Object);
        Assert.AreEqual(400, (await reloaded.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(0, client.Invocations.Count);
        context.Request.Endpoint = Endpoint;
        Assert.AreEqual(200, (await reloaded.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    private static HttpResponseMessage Reply(string body, bool stream = false) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };

    private static Mock<IPluginHttpClient> Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) => reply(request));
        return client;
    }
}
