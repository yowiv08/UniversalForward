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
public sealed class ClientHeaderForwardingTests
{
    private const string Done = """{"type":"response.completed","response":{"id":"resp_headers","status":"completed","output":[]}}""";
    private const string ClientAgent = "downstream-client/1.0 (original agent)";

    [TestMethod]
    [DataRow("http", "/v1/responses", false)]
    [DataRow("http", "/v1/responses", true)]
    [DataRow("http", "/v1/messages", false)]
    [DataRow("http", "/v1/messages", true)]
    [DataRow("websocket", "/v1/responses", false)]
    [DataRow("websocket", "/v1/responses", true)]
    public async Task DeletingOverridesRestoresAllClientHeadersOnTheWire(string transport, string endpoint, bool perEndpoint)
    {
        var observed = new List<Dictionary<string, string>>();
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            observed.Add(peer.Headers);
            if (transport == "websocket")
            {
                using var socket = await peer.AcceptAsync(ct);
                await WebSocketLoopback.ReceiveAsync(socket, ct);
                await WebSocketLoopback.SendAsync(socket, Done, ct);
            }
            else
            {
                await peer.ReadBodyAsync(ct);
                await peer.RejectAsync(200, endpoint == "/v1/responses" ? """{"status":"completed","output":[]}"""
                    : """{"content":[{"type":"text","text":"OK"}]}""", ct);
            }
        }, connections: 2);
        var account = Account(server.Address, transport);
        var configured = new JsonObject
        {
            ["user-agent"] = "configured-agent/2.0", ["X-Custom"] = "configured-custom",
            ["Session-Id"] = "configured-session", ["OpenAI-Beta"] = "configured-beta"
        };
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => server.CreateClient());
        using var http = server.CreateClient();
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption option, CancellationToken ct) => http.SendAsync(request, option, ct));
        using var terminal = new UniversalForwardTerminal(host);
        var firstSave = await terminal.SaveAccountAsync(ChannelKeysTests.Context(Configuration(configured)));
        Assert.AreEqual(200, firstSave.StatusCode);
        var context = Context(account, client.Object, endpoint);
        var first = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, first.Response.StatusCode, JsonSerializer.Serialize(first.Response));

        // Simulate deleting the rows and saving the complete current header map.
        var secondSave = await terminal.SaveAccountAsync(ChannelKeysTests.Context(Configuration(new JsonObject())));
        Assert.AreEqual(200, secondSave.StatusCode);
        Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
        await server.Completion;

        Assert.AreEqual("configured-agent/2.0", observed[0].GetValueOrDefault("User-Agent"));
        Assert.AreEqual("configured-custom", observed[0].GetValueOrDefault("X-Custom"));
        Assert.AreEqual("configured-session", observed[0].GetValueOrDefault("Session-Id"));
        Assert.AreEqual("configured-beta", observed[0].GetValueOrDefault("OpenAI-Beta"));
        var restored = observed[1];
        Assert.AreEqual(ClientAgent, restored.GetValueOrDefault("User-Agent"));
        Assert.AreEqual("client-custom", restored.GetValueOrDefault("X-Custom"));
        Assert.AreEqual("client-session", restored.GetValueOrDefault("Session-Id"));
        Assert.AreEqual("client-beta", restored.GetValueOrDefault("OpenAI-Beta"));
        Assert.AreEqual("zh-CN, en;q=0.7", restored.GetValueOrDefault("Accept-Language"));
        Assert.AreEqual("https://client.example", restored.GetValueOrDefault("Origin"));
        Assert.AreEqual("client-extra", restored.GetValueOrDefault("X-Unlisted"));
        if (transport == "http") Assert.AreEqual("application/json; charset=utf-8", restored.GetValueOrDefault("Content-Type"));
        Assert.AreEqual(endpoint == "/v1/messages" ? "channel-key" : "Bearer channel-key",
            restored.GetValueOrDefault(endpoint == "/v1/messages" ? "x-api-key" : "Authorization"));
        Assert.IsFalse(restored.ContainsKey("Cookie"));
        Assert.IsFalse(restored.ContainsKey("Proxy-Authorization"));
        Assert.IsFalse(restored.ContainsKey("X-Hop"));
        Assert.AreNotEqual("wrong-client-host", restored.GetValueOrDefault("Host"));
        Assert.AreNotEqual("99999", restored.GetValueOrDefault("Content-Length"));
        Assert.AreEqual(ClientAgent, context.Request.RequestHeaders["User-Agent"]);

        object Configuration(JsonObject headers) => perEndpoint
            ? new { id = account.Id, label = account.Label, headerOverrideMode = "perEndpoint",
                headerOverride = new JsonObject { ["User-Agent"] = "unused-common" },
                endpointHeaderOverrides = new Dictionary<string, EndpointHeaderOverride>
                { [endpoint] = new() { UseCommon = false, Headers = headers } } }
            : new { id = account.Id, label = account.Label, headerOverride = headers };
    }

    private static Account Account(Uri address, string transport)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "channel-key" }]);
        var settings = ChannelKeysTests.Settings(account);
        settings["baseUrl"] = address.GetLeftPart(UriPartial.Authority);
        settings["responsesTransport"] = transport;
        settings["headerOverride"] = new JsonObject();
        settings["requestPolicy"] = JsonSerializer.SerializeToNode(new ForwardRequestPolicy
        { HeaderTimeoutSeconds = 5, StreamIdleTimeoutSeconds = 5, TotalTimeoutSeconds = 15 }, RequestLogStore.Json);
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() });
        return account;
    }

    private static PluginAttemptContext Context(Account account, IPluginHttpClient client, string endpoint)
    {
        var context = ChannelKeysTests.Attempt(account, client);
        context.Request.Endpoint = endpoint;
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(new { model = "model", input = "question" });
        // Match the host: the regular map is filtered, the diagnostic snapshot retains full values.
        context.Request.RequestHeaders["uSeR-aGeNt"] = ClientAgent;
        context.Request.RequestHeaders["X-Custom"] = "client-custom";
        context.Request.RequestHeaders["X-Unlisted"] = "client-extra";
        var headers = context.Request.DownstreamRequestHeaders;
        headers["User-Agent"] = [ClientAgent];
        headers["X-Custom"] = ["client-custom"];
        headers["Session-Id"] = ["client-session"];
        headers["OpenAI-Beta"] = ["client-beta"];
        headers["Accept-Language"] = ["zh-CN", "en;q=0.7"];
        headers["Origin"] = ["https://client.example"];
        headers["Content-Type"] = ["application/json; charset=utf-8"];
        headers["Authorization"] = ["Bearer router-client-key"];
        headers["x-api-key"] = ["router-client-key"];
        headers["Cookie"] = ["router-session=private"];
        headers["Proxy-Authorization"] = ["Basic private"];
        headers["Connection"] = ["X-Hop"];
        headers["X-Hop"] = ["private-hop"];
        headers["Host"] = ["wrong-client-host"];
        headers["Content-Length"] = ["99999"];
        return context;
    }
}
