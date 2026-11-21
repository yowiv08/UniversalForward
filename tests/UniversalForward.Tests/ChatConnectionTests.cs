using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

public sealed partial class ConnectionTestsTests
{
    private const string ChatEndpoint = "/v1/chat/completions";
    private const string ChatDelta = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":null}]}\n\n";
    private const string ChatFinish = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string ChatUsage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\n";
    private const string ChatDone = "data: [DONE]\n\n";

    [TestMethod]
    [DataRow("""{"choices":[{"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"content":null,"reasoning_content":"Thinking"},"finish_reason":"stop"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"content":null,"reasoning":"Thinking"},"finish_reason":"stop"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"content":null,"reasoning_details":[{"type":"reasoning.encrypted","data":"opaque"}]},"finish_reason":"stop"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"content":null,"refusal":"Cannot answer"},"finish_reason":"stop"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"content":null,"audio":{"data":"aGVsbG8=","transcript":"Hello"}},"finish_reason":"stop"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"function_call":{"name":"lookup","arguments":"{}"}},"finish_reason":"function_call"}]}""", true)]
    [DataRow("""{"choices":[{"message":{"content":"OK"},"finish_reason":"length"}]}""", false)]
    [DataRow("""{"choices":[{"message":{"content":"OK"},"finish_reason":"content_filter"}]}""", false)]
    [DataRow("""{"choices":[{"message":{"content":"OK"},"finish_reason":null}]}""", false)]
    [DataRow("""{"choices":[{"message":{"content":"OK"},"finish_reason":42}]}""", false)]
    [DataRow("""{"choices":[{"message":{"role":"assistant","content":""},"finish_reason":"stop"}]}""", false)]
    [DataRow("""{"choices":[{"message":{"tool_calls":[{}]},"finish_reason":"tool_calls"}]}""", false)]
    [DataRow("""{"choices":[{"delta":{"content":"OK"},"finish_reason":"stop"}]}""", false)]
    [DataRow("""{"choices":[],"usage":{"total_tokens":5}}""", false)]
    [DataRow("""{"error":{"message":"denied"},"choices":[{"message":{"content":"OK"},"finish_reason":"stop"}]}""", false)]
    [DataRow("""{"choices":[null]}""", false)]
    public void ChatCompletionRequiresOutputAndSuccessfulFinish(string body, bool expected)
        => Assert.AreEqual(expected, UniversalForwardTerminal.IsTestCompletion(Encoding.UTF8.GetBytes(body), ChatEndpoint));

    [TestMethod]
    [DataRow(ChatDelta + ChatFinish + ChatUsage + ChatDone, true, null)]
    [DataRow(ChatDelta + ChatFinish + ChatDone, true, null)]
    [DataRow("data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Thinking\"},\"finish_reason\":null}]}\n\n" + ChatFinish + ChatDone, true, null)]
    [DataRow("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"lookup\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" + ChatDone, true, null)]
    [DataRow(ChatDone, false, null)]
    [DataRow(ChatUsage + ChatDone, false, null)]
    [DataRow(ChatFinish + ChatDone, false, null)]
    [DataRow(ChatDelta + ChatDone, false, null)]
    [DataRow(ChatDelta + ChatFinish, false, null)]
    [DataRow(ChatDelta, false, null)]
    [DataRow("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"finish_reason\":\"stop\"}]}\n\n" + ChatDone, false, null)]
    [DataRow(ChatDelta + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" + ChatDone, false, "上游响应未完成：length")]
    [DataRow(ChatDelta + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"content_filter\"}]}\n\n" + ChatDone, false, "上游响应未完成：content_filter")]
    [DataRow("data: {\"error\":{\"message\":\"rate limit exceeded\"}}\n\n" + ChatDone, false, "rate limit exceeded")]
    [DataRow(ChatDelta + ChatFinish + "data: {\"error\":{\"message\":\"late failure\"}}\n\n" + ChatDone, false, "upstream_stream_error: late failure")]
    [DataRow(ChatDelta + ChatFinish + "data: {\"partial\":", false, null)]
    public async Task ChatStreamTestChecksOutputFinishAndDoneWithoutLosingUsage(string body, bool expected, string? expectedError)
    {
        var host = Host();
        using var handler = new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = "account", models = SingleModel, endpoint = ChatEndpoint, stream = true }),
            _ => { }, CancellationToken.None));
        var row = ConnectionTestAssertions.Rows(terminal)[0];
        Assert.AreEqual(expected, row.GetProperty("success").GetBoolean(), row.ToString());
        if (expectedError is not null) Assert.AreEqual(expectedError, row.GetProperty("error").GetString());
        if (expected) Assert.AreNotEqual(JsonValueKind.Null, row.GetProperty("firstEventMs").ValueKind);
        Assert.AreEqual(body, row.GetProperty("response").GetString());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ChatConnectionTestBuildsNativeBodyAndUsesSavedSettings(bool stream, bool explicitPreference)
    {
        var host = Host();
        var account = (await host.Services.Accounts.GetAsync("account", CancellationToken.None))!;
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["endpoints"] = new JsonArray(ChatEndpoint);
            settings["responsesTransport"] = "websocket";
            settings["stripResponsesResourceState"] = true;
            settings["systemPromptPolicy"] = new JsonObject { ["mode"] = "append", ["text"] = "Channel instructions" };
            settings["reasoningPolicy"] = JsonNode.Parse("""{"defaults":{"chat":{"mode":"map","defaultEffort":"high"}}}""");
            if (explicitPreference)
                settings["modelProtocols"] = JsonNode.Parse("""{"a":{"protocols":["chat"],"preferredProtocol":"chat","nativeOnly":true}}""");
        });
        var sent = 0;
        using var handler = new ProfileHandler(async request =>
        {
            sent++;
            Assert.AreEqual(ChatEndpoint, request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer test-only", request.Headers.Authorization!.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.AreEqual("a", body["model"]!.ToString());
            Assert.AreEqual(stream, body["stream"]!.GetValue<bool>());
            Assert.AreEqual(256, body["max_completion_tokens"]!.GetValue<int>());
            Assert.AreEqual(2, body["messages"]!.AsArray().Count);
            Assert.AreEqual("system", body["messages"]![0]!["role"]!.ToString());
            Assert.AreEqual("Channel instructions", body["messages"]![0]!["content"]!.ToString());
            Assert.AreEqual("user", body["messages"]![1]!["role"]!.ToString());
            Assert.AreEqual("Test Chat Completions", body["messages"]![1]!["content"]!.ToString());
            Assert.AreEqual("high", body["reasoning_effort"]!.ToString());
            Assert.IsNull(body["input"]);
            Assert.IsNull(body["max_tokens"]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream
                ? ChatDelta + ChatFinish + ChatUsage + ChatDone
                : """{"choices":[{"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""",
                Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        Mock.Get(host.Services.Jobs).Setup(x => x.StartAsync("connection-test", It.IsAny<JsonElement?>(),
            It.IsAny<PluginJobOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        using var terminal = new UniversalForwardTerminal(host);
        var input = new { accountId = "account", models = SingleModel, stream, content = "Test Chat Completions", maxOutputTokens = 256 };
        Assert.AreEqual(202, (await terminal.StartConnectionTestsAsync(Context(input))).StatusCode);
        await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(input), _ => { }, CancellationToken.None));
        var row = ConnectionTestAssertions.Rows(terminal)[0];
        Assert.IsTrue(row.GetProperty("success").GetBoolean(), row.ToString());
        Assert.AreEqual(ChatEndpoint, row.GetProperty("endpoint").GetString());
        Assert.AreEqual("http", row.GetProperty("transport").GetString());
        Assert.AreEqual(1, sent);
    }
}
