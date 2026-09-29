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
public sealed class EndpointRoutingTests
{
    [TestMethod]
    [DataRow("universalforward/model")]
    [DataRow("vendor/model")]
    public async Task HostNormalizedUpstreamModelKeepsItsPlatformPrefix(string upstreamModel)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                Assert.AreEqual(upstreamModel, body["model"]!.GetValue<string>());
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"status":"completed","output":[]}""") };
            });
        var context = ForwardResponseHandlingTests.Context(client.Object, "codex", false);
        var fields = new Dictionary<string, string?>(((CustomCredential)context.Account.Credential).Fields)
        { ["models"] = JsonSerializer.Serialize(new[] { upstreamModel }) };
        context.Account.Credential = new CustomCredential(fields);
        context.Request.Model = upstreamModel;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("/v1/chat/completions")]
    [DataRow("/v1/completions")]
    public async Task LegacyEndpointsNeverReachUpstream(string endpoint)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        var context = ForwardResponseHandlingTests.Context(client.Object, "codex", false);
        context.Request.Endpoint = endpoint;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        Assert.AreEqual(400, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(0, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("/v1/responses", "messages", "/v1/messages", false)]
    [DataRow("/v1/responses", "messages", "/v1/messages", true)]
    [DataRow("/v1/messages", "responses", "/v1/responses", false)]
    [DataRow("/v1/messages", "responses", "/v1/responses", true)]
    public async Task SwitchesOnlyEndpointAuthenticationAndModel(string incoming, string protocol, string target, bool stream)
    {
        var original = JsonNode.Parse("""
            {"model":"universalforward/vendor/model","messages":[{"role":"user","content":"hello"}],
             "tools":[{"custom":"unchanged"}],"max_tokens":17,"unknown":{"n":1.25},
             "endpoint":"preserve","overrides":{"x":1},"models":["keep"]}
            """)!.AsObject();
        original["stream"] = stream;
        var upstreamBytes = Encoding.UTF8.GetBytes(stream
            ? "event: message_start\ndata: {\"type\":\"message_start\"}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
            : " { \"type\":\"message\", \"content\":[{\"type\":\"text\",\"text\":\"unchanged\"}] } ");
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                Assert.AreEqual("/gateway" + target, request.RequestUri!.AbsolutePath);
                var expected = original.DeepClone().AsObject();
                expected["model"] = "vendor/model";
                var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.IsTrue(JsonNode.DeepEquals(expected, sent));
                if (target == "/v1/messages")
                {
                    Assert.AreEqual("channel-key", request.Headers.GetValues("x-api-key").Single());
                    Assert.AreEqual("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
                    Assert.IsFalse(request.Headers.Contains("Authorization"));
                }
                else
                {
                    Assert.AreEqual("Bearer channel-key", request.Headers.GetValues("Authorization").Single());
                    Assert.IsFalse(request.Headers.Contains("x-api-key"));
                }
                var content = new ByteArrayContent(upstreamBytes);
                content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
        var account = new Account
        {
            Id = "channel", PluginKey = "universalforward", Platform = "universalforward",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = JsonSerializer.Serialize(new
                {
                    baseUrl = "https://upstream.example/gateway/v1", apiKey = "channel-key",
                    modelProtocols = new Dictionary<string, ModelProtocolOptions>
                    { ["vendor/model"] = new() { Protocols = [protocol], PreferredProtocol = protocol } }
                }),
                ["models"] = """["vendor/model"]""", ["modelsConfigured"] = "true"
            })
        };
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(new PluginAttemptContext
        {
            Account = account, HttpClient = client.Object, PluginKey = "universalforward", PlatformName = "universalforward",
            CancellationToken = CancellationToken.None,
            Request = new AdapterRequest
            {
                Model = "universalforward/vendor/model", Endpoint = incoming, Stream = stream,
                OriginalBody = JsonSerializer.SerializeToElement(original),
                RequestHeaders = { ["Authorization"] = "client-secret", ["x-api-key"] = "client-secret" }
            }
        });
        Assert.AreEqual(200, result.Response.StatusCode);
        if (stream)
        {
            using var output = new System.IO.MemoryStream();
            await foreach (var bytes in result.Response.RawStream!) await output.WriteAsync(bytes);
            CollectionAssert.AreEqual(upstreamBytes, output.ToArray());
            await result.Response.Lifetime!.DisposeAsync();
        }
        else CollectionAssert.AreEqual(upstreamBytes, result.Response.RawContent!);
        Assert.AreEqual("universalforward/vendor/model", original["model"]!.GetValue<string>());
        Assert.AreEqual(1, client.Invocations.Count);
    }
}
