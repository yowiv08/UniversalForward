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
public sealed class ReasoningPolicyTests
{
    private static readonly string[] Models = ["model"];
    private const string ResponseJson = """{"id":"response","status":"completed","reasoning":{"effort":"high"},"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""";
    private const string ResponsesSse = "data: {\"type\":\"response.completed\",\"response\":" + ResponseJson + "}\n\n";
    private const string MessagesSse = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"message\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"usage\":{\"input_tokens\":1}}}\n\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
        + "data: {\"type\":\"message_stop\"}\n\n";

    private static ReasoningPolicy Policy(string protocol = "responses", string? fallback = null) => new()
    {
        Defaults = new() { [protocol] = new() { Mode = "map", DefaultEffort = fallback,
            Mappings = [new() { From = " low ", To = " max " }, new() { From = "max", To = "high" }] } }
    };
    internal static void Edit(Account account, Action<JsonObject> edit)
    {
        var settings = ChannelKeysTests.Settings(account); edit(settings);
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() });
    }
    private static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();

    [TestMethod]
    [DataRow("responses", "reasoning")]
    [DataRow("messages", "output_config")]
    public void MapsOnceAndPreservesOtherParametersAndHistory(string protocol, string field)
    {
        var policy = Policy(protocol); policy.Validate();
        var original = Object("""{"input":[{"type":"configuration_update","reasoning":{"effort":"low"}}],"thinking":{"type":"enabled","budget_tokens":2048},"max_tokens":4096}""");
        original[field] = new JsonObject { ["effort"] = " LOW ", ["summary"] = "auto", ["custom"] = 3 };
        var expected = original.DeepClone(); expected[field]!["effort"] = "max";
        var prepared = (JsonObject)original.DeepClone();
        var result = policy.Resolve(original, "model", "/v1/" + protocol)!;
        ReasoningPolicy.Apply(prepared, result);
        Assert.AreEqual("max", result.Sent);
        Assert.AreEqual("mapped", result.Reason);
        Assert.IsTrue(JsonNode.DeepEquals(expected, prepared));
        Assert.AreEqual(" LOW ", original[field]!["effort"]!.GetValue<string>());
        Assert.IsNull(policy.Resolve(Object($"{{\"{field}\":{{\"effort\":\"unknown\"}}}}"), "model", "/v1/" + protocol));
    }

    [TestMethod]
    [DataRow("{}", "high")]
    [DataRow("{\"reasoning\":null}", "high")]
    [DataRow("{\"reasoning\":{}}", "high")]
    [DataRow("{\"reasoning\":{\"effort\":null}}", "high")]
    [DataRow("{\"reasoning\":{\"effort\":\"\"}}", null)]
    [DataRow("{\"reasoning\":{\"effort\":12}}", null)]
    [DataRow("{\"reasoning\":\"invalid\"}", null)]
    public void DefaultRequiresAbsentOrNullClientLevel(string json, string? expected)
    {
        var policy = Policy(fallback: "high"); policy.Validate();
        Assert.AreEqual(expected, policy.Resolve(Object(json), "model", "/v1/responses")?.Sent);
        policy.Defaults["responses"].DefaultEffort = null;
        Assert.IsNull(policy.Resolve(Object(json), "model", "/v1/responses"));
    }

    [TestMethod]
    public void ModelOverridesReplaceWholeRuleAndRemainIndependentByProtocol()
    {
        var policy = Policy(fallback: "max");
        policy.Defaults["messages"] = new() { Mode = "fixed", FixedEffort = "vendor-level" };
        policy.Models["model"] = new() { ["responses"] = new() { Mode = "map", Mappings = [new() { From = "medium", To = "xhigh" }] } };
        policy.Validate(["model"]);
        Assert.IsNull(policy.Resolve(Object("{}"), "model", "/v1/responses"));
        Assert.IsNull(policy.Resolve(Object("{\"reasoning\":{\"effort\":\"low\"}}"), "model", "/v1/responses"));
        var mapped = policy.Resolve(Object("{\"reasoning\":{\"effort\":\"medium\"}}"), "model", "/v1/responses")!;
        Assert.AreEqual("model", mapped.Source); Assert.AreEqual("xhigh", mapped.Sent);
        Assert.AreEqual("vendor-level", policy.Resolve(Object("{}"), "model", "/v1/messages")!.Sent);
        policy.Models["model"]["responses"].Mode = "off";
        Assert.IsNull(policy.Resolve(Object("{\"reasoning\":{\"effort\":\"medium\"}}"), "model", "/v1/responses"));
        policy.Models["model"].Remove("responses");
        Assert.AreEqual("channel", policy.Resolve(Object("{}"), "model", "/v1/responses")!.Source);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"reasoning\":{\"effort\":\"none\"}}")]
    [DataRow("{\"reasoning\":{\"effort\":42}}")]
    public void FixedLevelReplacesProvidedAndMissingLevels(string json)
    {
        var policy = new ReasoningPolicy { Defaults = new() { ["responses"] = new() { Mode = "fixed", FixedEffort = "ultra" } } };
        policy.Validate();
        var original = Object(json); var prepared = (JsonObject)original.DeepClone();
        ReasoningPolicy.Apply(prepared, policy.Resolve(original, "model", "/v1/responses")!);
        Assert.AreEqual("ultra", prepared["reasoning"]!["effort"]!.ToString());
    }

    [TestMethod]
    public async Task SettingsRoundTripNormalizeAndSurviveOmittedField()
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }]);
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var legacy = JsonSerializer.SerializeToNode((await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body, RequestLogStore.Json)!;
        Assert.AreEqual(0, legacy["accounts"]![0]!["reasoningPolicy"]!["defaults"]!.AsObject().Count);
        var policy = Policy(fallback: " high ");
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, reasoningPolicy = policy }))).StatusCode);
        var saved = ChannelKeysTests.Settings(account)["reasoningPolicy"]!.ToJsonString();
        var rule = ChannelKeysTests.Settings(account)["reasoningPolicy"]!["defaults"]!["responses"]!;
        Assert.AreEqual("low", rule["mappings"]![0]!["from"]!.ToString());
        Assert.AreEqual("max", rule["mappings"]![0]!["to"]!.ToString());
        Assert.AreEqual("high", rule["defaultEffort"]!.ToString());
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, weight = 50 }))).StatusCode);
        Assert.AreEqual(saved, ChannelKeysTests.Settings(account)["reasoningPolicy"]!.ToJsonString());
        var listed = JsonSerializer.SerializeToNode((await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body, RequestLogStore.Json)!;
        Assert.AreEqual(saved, listed["accounts"]![0]!["reasoningPolicy"]!.ToJsonString());
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, reasoningPolicy = new ReasoningPolicy() }))).StatusCode);
        Assert.AreEqual(0, ChannelKeysTests.Settings(account)["reasoningPolicy"]!["defaults"]!.AsObject().Count);
    }

    [TestMethod]
    [DataRow("{\"defaults\":null}")]
    [DataRow("{\"models\":null}")]
    [DataRow("{\"models\":{\"foreign\":{}}}")]
    [DataRow("{\"defaults\":{\"chat\":{}}}")]
    [DataRow("{\"defaults\":{\"responses\":null}}")]
    [DataRow("{\"defaults\":{\"responses\":{\"mode\":\"invalid\"}}}")]
    [DataRow("{\"defaults\":{\"responses\":{\"mode\":\"fixed\",\"fixedEffort\":\" \"}}}")]
    [DataRow("{\"defaults\":{\"responses\":{\"mappings\":null}}}")]
    [DataRow("{\"defaults\":{\"responses\":{\"mappings\":[null]}}}")]
    [DataRow("{\"defaults\":{\"responses\":{\"mappings\":[{\"from\":\"low\",\"to\":\"\"}]}}}")]
    [DataRow("{\"defaults\":{\"responses\":{\"mappings\":[{\"from\":\"low\",\"to\":\"max\"},{\"from\":\" LOW \",\"to\":\"high\"}]}}}")]
    public async Task InvalidPolicyDoesNotWriteSettings(string json)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }]);
        var before = ChannelKeysTests.Settings(account).ToJsonString();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var result = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = account.Label, reasoningPolicy = Object(json) }));
        Assert.AreEqual(400, result.StatusCode);
        Assert.AreEqual(before, ChannelKeysTests.Settings(account).ToJsonString());
    }

    [TestMethod]
    [DataRow("responses", false, false)]
    [DataRow("responses", false, true)]
    [DataRow("responses", true, false)]
    [DataRow("responses", true, true)]
    [DataRow("messages", false, false)]
    [DataRow("messages", false, true)]
    [DataRow("messages", true, false)]
    [DataRow("messages", true, true)]
    public async Task FinalHttpPayloadAndRetriesUseMappedEffort(string protocol, bool stream, bool profile)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }], 1);
        Edit(account, settings => {
            settings["reasoningPolicy"] = JsonSerializer.SerializeToNode(Policy(protocol), RequestLogStore.Json);
            if (profile) settings["headerOverride"] = protocol == "responses" ? new JsonObject { ["Originator"] = "codex_exec" }
                : new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" };
        });
        var bodies = new List<string>();
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) => {
                bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                return bodies.Count == 1 ? new(HttpStatusCode.BadGateway) { Content = new StringContent("retry") }
                    : Reply(protocol, stream || profile);
            });
        using var store = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account)) { RequestLogs = store };
        var context = ChannelKeysTests.Attempt(account, client.Object);
        var field = protocol == "responses" ? "reasoning" : "output_config";
        var original = Object("""{"model":"model","input":"hello","messages":[{"role":"user","content":"hello"}],"thinking":{"type":"enabled","budget_tokens":2048},"max_tokens":4096}""");
        original[field] = new JsonObject { ["effort"] = "low", ["custom"] = true };
        original["stream"] = stream;
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(original);
        context.Request.Endpoint = "/v1/" + protocol; context.Request.Stream = stream;
        context.Request.Extensions["reasoning_effort"] = "minimal";
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        if (result.Response.RawStream is { } output) await foreach (var _ in output) { }
        Assert.AreEqual(2, bodies.Count); Assert.AreEqual(bodies[0], bodies[1]);
        var sent = Object(bodies[1]);
        Assert.AreEqual("max", sent[field]!["effort"]!.ToString());
        Assert.IsTrue(sent[field]!["custom"]!.GetValue<bool>());
        Assert.IsTrue(JsonNode.DeepEquals(original["thinking"], sent["thinking"]));
        Assert.AreEqual("low", context.Request.OriginalBody!.Value.GetProperty(field).GetProperty("effort").GetString());
        await store.FlushAsync();
        var row = JsonSerializer.SerializeToNode(store.List(new Dictionary<string, string>()), RequestLogStore.Json)!["rows"]![0]!;
        Assert.AreEqual("low", row["receivedReasoning"]![field + ".effort"]!.ToString());
        Assert.AreEqual("max", row["sentReasoning"]![field + ".effort"]!.ToString());
        Assert.AreEqual("channel", row["reasoningMapping"]!["source"]!.ToString());
        Assert.AreEqual(protocol, row["reasoningMapping"]!["protocol"]!.ToString());
        if (protocol == "responses") Assert.AreEqual("high", row["reportedReasoning"]!["reasoning.effort"]!.ToString());
    }

    [TestMethod]
    public async Task MappingUsesConfiguredModelAndActualUpstreamProtocol()
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }]);
        Edit(account, settings => {
            settings["modelProtocols"] = Object("{\"model\":{\"protocols\":[\"messages\"],\"preferredProtocol\":\"messages\"}}");
            settings["reasoningPolicy"] = Object("{\"defaults\":{\"responses\":{\"mode\":\"fixed\",\"fixedEffort\":\"wrong\"}},\"models\":{\"model\":{\"messages\":{\"mode\":\"fixed\",\"fixedEffort\":\"max\"}}}}");
        });
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) => {
                Assert.AreEqual("/v1/messages", request.RequestUri!.AbsolutePath);
                var sent = Object(await request.Content!.ReadAsStringAsync(ct));
                Assert.AreEqual("model", sent["model"]!.ToString());
                Assert.AreEqual("max", sent["output_config"]!["effort"]!.ToString());
                Assert.AreEqual("low", sent["reasoning"]!["effort"]!.ToString());
                return Reply("messages", false);
            });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.Model = "universalforward/model";
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(Object("{\"model\":\"universalforward/model\",\"reasoning\":{\"effort\":\"low\"}}"));
        Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(null, "low")]
    [DataRow("high", "high")]
    public async Task ConnectionTestsTreatTemplateDefaultAsMissingClientInput(string? fallback, string expected)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }]);
        Edit(account, settings => {
            settings["reasoningPolicy"] = JsonSerializer.SerializeToNode(Policy(fallback: fallback), RequestLogStore.Json);
            settings["headerOverride"] = new JsonObject { ["Originator"] = "codex_exec" };
        });
        var host = ChannelKeysTests.Host(account);
        JsonObject? sent = null;
        using var handler = new RequestHandler(async request => {
            sent = Object(await request.Content!.ReadAsStringAsync()); return Reply("responses", true);
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        PluginJobRegistration? registration = null;
        var builder = new Mock<IPluginBuilder>();
        builder.Setup(x => x.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(job => registration = job);
        terminal.Configure(builder.Object);
        var result = await registration!.ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = account.Id, models = Models, endpoint = "/v1/responses" }), _ => { }, CancellationToken.None));
        Assert.IsTrue(result!.Value.GetProperty("rows")[0].GetProperty("success").GetBoolean(), result.ToString());
        Assert.AreEqual(expected, sent!["reasoning"]!["effort"]!.ToString());
    }

    private static HttpResponseMessage Reply(string protocol, bool stream) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(stream ? protocol == "responses" ? ResponsesSse : MessagesSse : ResponseJson,
            Encoding.UTF8, stream ? "text/event-stream" : "application/json")
    };
    private sealed class RequestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }
}
