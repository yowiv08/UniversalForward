using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

[TestClass]
public sealed class AuthenticationSelectionTests
{
    internal static JsonObject Authentication(string header) => new()
    {
        ["apiKeyHeader"] = header,
        ["apiKeyPrefix"] = header == "Authorization" ? "Bearer " : "",
        ["vendor"] = new JsonObject { ["keep"] = true }
    };

    internal static void AssertAuthentication(HttpRequestMessage request, string header, string secret)
    {
        Assert.AreEqual(header == "Authorization" ? "Bearer " + secret : secret, request.Headers.GetValues(header).Single());
        Assert.IsFalse(request.Headers.Contains(header == "Authorization" ? "x-api-key" : "Authorization"));
    }

    [TestMethod]
    [DataRow("/v1/responses", "Authorization", false)]
    [DataRow("/v1/messages", "Authorization", false)]
    [DataRow("/v1/chat/completions", "Authorization", false)]
    [DataRow("/v1/responses", "x-api-key", false)]
    [DataRow("/v1/messages", "x-api-key", false)]
    [DataRow("/v1/chat/completions", "x-api-key", false)]
    [DataRow("/v1/responses", "Authorization", true)]
    [DataRow("/v1/messages", "Authorization", true)]
    [DataRow("/v1/chat/completions", "Authorization", true)]
    [DataRow("/v1/responses", "x-api-key", true)]
    [DataRow("/v1/messages", "x-api-key", true)]
    [DataRow("/v1/chat/completions", "x-api-key", true)]
    public async Task SavedSelectionOverridesProtocolDefaultAndSurvivesRetries(string endpoint, string header, bool stream)
    {
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }], retries: 1);
        var host = ChannelKeysTests.Host(account);
        var authentication = Authentication(header);
        using (var editor = new UniversalForwardTerminal(host))
        {
            Assert.AreEqual(200, (await editor.SaveAccountAsync(ChannelKeysTests.Context(new
            { id = account.Id, label = account.Label, extraParams = authentication }))).StatusCode);
            Assert.AreEqual(200, (await editor.SaveAccountAsync(ChannelKeysTests.Context(new
            { id = account.Id, label = account.Label, weight = 80 }))).StatusCode);
        }
        var saved = ChannelKeysTests.Settings(account)["extraParams"]!.GetValue<string>();
        Assert.IsTrue(JsonNode.DeepEquals(authentication, JsonNode.Parse(saved)));
        var bodies = new List<string>();
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                AssertAuthentication(request, header, "channel-key");
                Assert.AreEqual(endpoint, request.RequestUri!.AbsolutePath);
                bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                return bodies.Count == 1 ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("retry") }
                    : Response(endpoint, stream);
            });
        using var terminal = new UniversalForwardTerminal(host);
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.Endpoint = endpoint; context.Request.Stream = stream;
        context.Request.OriginalBody = UniversalForwardTerminal.CreateTestBody("model", endpoint, stream);
        context.Request.RequestHeaders["Authorization"] = "Bearer downstream-secret";
        context.Request.RequestHeaders["x-api-key"] = "downstream-secret";
        var original = context.Request.OriginalBody.Value.GetRawText();
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        if (result.Response.RawStream is { } output) await foreach (var _ in output) { }
        if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
        Assert.AreEqual(2, bodies.Count);
        Assert.AreEqual(bodies[0], bodies[1]);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(bodies[1])));
        Assert.AreEqual(original, context.Request.OriginalBody.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("Authorization")]
    [DataRow("x-api-key")]
    public async Task DiscoveryAndRefreshUseSelectedAuthentication(string header)
    {
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }]);
        ReasoningPolicyTests.Edit(account, settings => settings["extraParams"] = Authentication(header).ToJsonString());
        var host = ChannelKeysTests.Host(account);
        var sent = 0;
        using var handler = new Handler(request =>
        {
            sent++;
            Assert.AreEqual("/v1/models", request.RequestUri!.AbsolutePath);
            AssertAuthentication(request, header, sent == 1 ? "draft-key" : "channel-key");
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"model"}]}""") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        { baseUrl = "https://upstream.example/v1/chat/completions", apiKey = "draft-key", extraParams = Authentication(header) }))).StatusCode);
        Assert.AreEqual(200, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { id = account.Id }))).StatusCode);
        Assert.AreEqual(200, (await terminal.RefreshModelsAsync(ChannelKeysTests.Context(new { id = account.Id }))).StatusCode);
        Assert.AreEqual(3, sent);
    }

    [TestMethod]
    [DataRow("Authorization")]
    [DataRow("x-api-key")]
    public async Task WebSocketHandshakeUsesSelectedAuthentication(string header)
    {
        WebSocketLoopback.Peer? received = null;
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            received = peer;
            using var socket = await peer.AcceptAsync(ct);
            await WebSocketLoopback.ReceiveAsync(socket, ct);
            await WebSocketLoopback.SendAsync(socket, """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"OK"}]}]}}""", ct);
        });
        var account = ChannelKeysTests.Account([new() { Id = "key", Secret = "channel-key" }]);
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["baseUrl"] = server.Address.ToString().TrimEnd('/');
            settings["responsesTransport"] = "websocket";
            settings["extraParams"] = Authentication(header).ToJsonString();
        });
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => server.CreateClient());
        using var terminal = new UniversalForwardTerminal(host);
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        Assert.AreEqual(200, (await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, client.Object))).Response.StatusCode);
        await server.Completion;
        Assert.AreEqual(header == "Authorization" ? "Bearer channel-key" : "channel-key", received!.Headers[header]);
        Assert.IsFalse(received.Headers.ContainsKey(header == "Authorization" ? "x-api-key" : "Authorization"));
        Assert.AreEqual(0, client.Invocations.Count);
    }

    internal static HttpResponseMessage Response(string endpoint, bool stream) => new(HttpStatusCode.OK)
    {
        Content = new StringContent((endpoint, stream) switch
        {
            ("/v1/messages", false) => """{"content":[{"type":"text","text":"OK"}]}""",
            ("/v1/messages", true) => "data: {\"type\":\"message_start\"}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n",
            ("/v1/responses", false) => """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"OK"}]}]}""",
            ("/v1/responses", true) => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: {\"type\":\"response.completed\"}\n\n",
            (_, false) => """{"choices":[{"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""",
            _ => "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
        }, Encoding.UTF8, stream ? "text/event-stream" : "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
}

public sealed partial class ConnectionTestsTests
{
    [TestMethod]
    [DataRow("/v1/responses", "Authorization", false)]
    [DataRow("/v1/messages", "Authorization", true)]
    [DataRow("/v1/chat/completions", "Authorization", false)]
    [DataRow("/v1/responses", "x-api-key", true)]
    [DataRow("/v1/messages", "x-api-key", false)]
    [DataRow("/v1/chat/completions", "x-api-key", true)]
    public async Task ConnectionTestHonorsSavedAuthentication(string endpoint, string header, bool stream)
    {
        var host = Host();
        var account = (await host.Services.Accounts.GetAsync("account", CancellationToken.None))!;
        ReasoningPolicyTests.Edit(account, settings => settings["extraParams"] = AuthenticationSelectionTests.Authentication(header).ToJsonString());
        var sent = 0;
        using var handler = new ProfileHandler(request =>
        {
            sent++; AuthenticationSelectionTests.AssertAuthentication(request, header, "test-only");
            return Task.FromResult(AuthenticationSelectionTests.Response(endpoint, stream));
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = "account", models = SingleModel, endpoint, stream }), _ => { }, CancellationToken.None));
        var row = ConnectionTestAssertions.Rows(terminal)[0];
        Assert.IsTrue(row.GetProperty("success").GetBoolean(), row.ToString());
        Assert.AreEqual(1, sent);
    }
}
