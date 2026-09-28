using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class HeaderOverridesTests
{
    [TestMethod]
    public void WildcardSkipsCredentialsAndExplicitValuesWin()
    {
        var rules = JsonNode.Parse("""{"*":true,"X-Trace":"fixed","Authorization":"Bearer {api_key}"}""")!.AsObject();
        var result = HeaderOverrides.Resolve(rules, new Dictionary<string, string>
        {
            ["X-Trace"] = "client", ["X-Other"] = "other", ["Authorization"] = "client-key",
            ["X-Api-Key"] = "client-key", ["Cookie"] = "cookie",
            ["Connection"] = "X-Hop", ["X-Hop"] = "private"
        }, "channel-key");
        Assert.AreEqual("fixed", result["X-Trace"]);
        Assert.AreEqual("other", result["X-Other"]);
        Assert.AreEqual("Bearer channel-key", result["Authorization"]);
        Assert.IsFalse(result.ContainsKey("X-Api-Key"));
        Assert.IsFalse(result.ContainsKey("Cookie"));
        Assert.IsFalse(result.ContainsKey("X-Hop"));
    }

    [TestMethod]
    public void ClientPlaceholderIsNotInterpolatedAgain()
    {
        var rules = JsonNode.Parse("""{"X-Value":"{client_header:X-Original}","X-Missing":"{client_header:Missing}"}""")!.AsObject();
        var result = HeaderOverrides.Resolve(rules,
            new Dictionary<string, string> { ["x-original"] = "{api_key}" }, "secret");
        Assert.AreEqual("{api_key}", result["X-Value"]);
        Assert.IsFalse(result.ContainsKey("X-Missing"));
    }

    [TestMethod]
    public void ChannelTestsSkipClientRules()
    {
        var rules = JsonNode.Parse("""{"*":true,"X-Copy":"{client_header:X-Original}","Authorization":"Bearer {api_key}"}""")!.AsObject();
        var result = HeaderOverrides.Resolve(rules,
            new Dictionary<string, string> { ["X-Original"] = "admin" }, "channel", true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Bearer channel", result["Authorization"]);
    }

    [TestMethod]
    [DataRow("re:^X-Trace-.*$")]
    [DataRow("regex:^X-Trace-.*$")]
    public void RegexSelectsHeaderNames(string key)
    {
        var result = HeaderOverrides.Resolve(new JsonObject { [key] = false },
            new Dictionary<string, string> { ["X-Trace-Id"] = "yes", ["X-Other"] = "no" }, "key");
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("yes", result["X-Trace-Id"]);
    }

    [TestMethod]
    [DataRow("""{"X-Test":"{client_header:X-Test}suffix"}""")]
    [DataRow("""{"X-Test":"{client_header:}"}""")]
    [DataRow("""{"X-Test":123}""")]
    [DataRow("""{"re:":true}""")]
    [DataRow("""{"re:[":true}""")]
    [DataRow("""{"Host":"bad"}""")]
    public void RejectsInvalidRules(string json)
        => Assert.Throws<FormatException>(() => HeaderOverrides.Validate(JsonNode.Parse(json)!.AsObject()));
}
