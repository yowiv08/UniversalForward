using System.Text.Json.Nodes;
using Plugins.UniversalForward;

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
    public void OnlyExplicitHeadersAreCopiedAndConnectionNominationsAreRemoved()
    {
        var extra = JsonNode.Parse("""{"PassThroughHeaders":["X-Trace","X-Local","User-Agent","X-Missing"]}""")!.AsObject();
        ForwardHeaders.Validate(extra);
        using var request = new HttpRequestMessage();
        ForwardHeaders.ApplySelected(request, new Dictionary<string, string>
        {
            ["x-trace"] = "trace", ["Connection"] = "X-Local", ["X-Local"] = "private",
            ["User-Agent"] = "client", ["Authorization"] = "secret", ["X-Unlisted"] = "unlisted"
        }, extra);
        Assert.AreEqual("trace", request.Headers.GetValues("X-Trace").Single());
        Assert.AreEqual("client", request.Headers.GetValues("User-Agent").Single());
        Assert.IsFalse(request.Headers.Contains("X-Local"));
        Assert.IsFalse(request.Headers.Contains("Authorization"));
        Assert.IsFalse(request.Headers.Contains("X-Unlisted"));
        Assert.IsFalse(request.Headers.Contains("X-Missing"));
    }

    [TestMethod]
    public void DefaultDoesNotCopyClientHeaders()
    {
        using var request = new HttpRequestMessage();
        ForwardHeaders.ApplySelected(request, new Dictionary<string, string> { ["User-Agent"] = "client" }, new());
        Assert.AreEqual(0, request.Headers.Count());
    }

    [TestMethod]
    public void ClientControlCharactersAreRejected()
    {
        using var request = new HttpRequestMessage();
        var extra = JsonNode.Parse("""{"PassThroughHeaders":["X-Trace"]}""")!.AsObject();
        Assert.Throws<FormatException>(() => ForwardHeaders.ApplySelected(request,
            new Dictionary<string, string> { ["X-Trace"] = "trace\r\nInjected: yes" }, extra));
    }
}
