using System.Text.Json.Nodes;
using Plugins.UniversalForward;
using Router.Contracts.Domain;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ForwardHeadersTests
{
    [TestMethod]
    [DataRow("""{"PassThroughHeaders":["Authorization"]}""")]
    [DataRow("""{"PassThroughHeaders":["x-api-key"]}""")]
    [DataRow("""{"PassThroughHeaders":["Cookie"]}""")]
    [DataRow("""{"PassThroughHeaders":["*"]}""")]
    [DataRow("""{"PassThroughHeaders":null}""")]
    [DataRow("""{"PassThroughHeaders":"User-Agent"}""")]
    [DataRow("""{"PassThroughHeaders":[42]}""")]
    [DataRow("""{"apiKeyHeader":"X-Secret","PassThroughHeaders":["x-secret"]}""")]
    [DataRow("""{"apiKeyHeader":"Host"}""")]
    [DataRow("""{"apiKeyHeader":"Content-Type"}""")]
    [DataRow("""{"apiKeyPrefix":"Bearer\r\nInjected: yes"}""")]
    [DataRow("""{"apiKeyHeader":null}""")]
    public void UnsafeConfigurationsAreRejected(string json)
        => Assert.Throws<FormatException>(() => ForwardHeaders.Validate(JsonNode.Parse(json)!.AsObject()));

    [TestMethod]
    public void AllClientHeadersAreCopiedAndConnectionNominationsAreRemoved()
    {
        var extra = JsonNode.Parse("""{"PassThroughHeaders":["X-Trace","X-Local","User-Agent","X-Missing"]}""")!.AsObject();
        ForwardHeaders.Validate(extra);
        using var request = new HttpRequestMessage();
        var source = new AdapterRequest();
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["x-trace"] = "trace", ["Connection"] = "X-Local", ["X-Local"] = "private",
            ["User-Agent"] = "client", ["Authorization"] = "secret", ["X-Unlisted"] = "unlisted"
        }) source.RequestHeaders[name] = value;
        ForwardHeaders.ApplyClientHeaders(request, ForwardHeaders.ReadClientHeaders(source, extra));
        Assert.AreEqual("trace", request.Headers.GetValues("X-Trace").Single());
        Assert.AreEqual("client", request.Headers.GetValues("User-Agent").Single());
        Assert.IsFalse(request.Headers.Contains("X-Local"));
        Assert.IsFalse(request.Headers.Contains("Authorization"));
        Assert.AreEqual("unlisted", request.Headers.GetValues("X-Unlisted").Single());
        Assert.IsFalse(request.Headers.Contains("X-Missing"));
    }

    [TestMethod]
    public void DefaultCopiesClientHeadersWithoutExtraConfiguration()
    {
        using var request = new HttpRequestMessage();
        var source = new AdapterRequest { RequestHeaders = { ["User-Agent"] = "client" } };
        ForwardHeaders.ApplyClientHeaders(request, ForwardHeaders.ReadClientHeaders(source, new()));
        Assert.AreEqual("client", request.Headers.GetValues("User-Agent").Single());
    }

    [TestMethod]
    public void FullSnapshotPreservesRepeatedValuesWithoutForwardingLocalCredentials()
    {
        var source = new AdapterRequest { RequestHeaders = { ["X-Only-In-Summary"] = "summary", ["X-Repeat"] = "combined" } };
        source.DownstreamRequestHeaders["x-repeat"] = ["one", "two"];
        source.DownstreamRequestHeaders["OpenAI-Beta"] = ["beta"];
        source.DownstreamRequestHeaders["X-Upstream-Key"] = ["downstream-secret"];
        source.DownstreamRequestHeaders["Cookie"] = ["router-session"];
        source.DownstreamRequestHeaders["X-Csrf-Token"] = ["router-csrf"];
        source.DownstreamRequestHeaders["Content-Encoding"] = ["gzip"];
        source.DownstreamRequestHeaders["Connection"] = ["OpenAI-Beta"];
        var headers = ForwardHeaders.ReadClientHeaders(source, new JsonObject { ["apiKeyHeader"] = "X-Upstream-Key" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example") { Content = new StringContent("{}") };
        ForwardHeaders.ApplyClientHeaders(request, headers);
        CollectionAssert.AreEqual(source.DownstreamRequestHeaders["x-repeat"], request.Headers.NonValidated["X-Repeat"].ToArray());
        Assert.AreEqual(2, headers.Count);
        Assert.AreEqual("summary", request.Headers.GetValues("X-Only-In-Summary").Single());
        Assert.AreEqual("downstream-secret", source.DownstreamRequestHeaders["X-Upstream-Key"][0]);
    }

    [TestMethod]
    public void ClientControlCharactersAreRejected()
    {
        using var request = new HttpRequestMessage();
        var extra = JsonNode.Parse("""{"PassThroughHeaders":["X-Trace"]}""")!.AsObject();
        var source = new AdapterRequest { RequestHeaders = { ["X-Trace"] = "trace\r\nInjected: yes" } };
        Assert.Throws<FormatException>(() => ForwardHeaders.ApplyClientHeaders(request,
            ForwardHeaders.ReadClientHeaders(source, extra)));
    }
}
