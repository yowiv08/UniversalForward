using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class EndpointHeaderOverrideTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void SelectionKeepsInheritanceIndependentAndEmptyHeadersDistinct()
    {
        var common = new JsonObject { ["X-Common"] = "one" };
        var custom = new JsonObject { ["X-Custom"] = "two" };
        var endpoints = new Dictionary<string, EndpointHeaderOverride>
        {
            ["/v1/responses"] = new() { UseCommon = false, Headers = custom },
            ["/v1/messages"] = new() { Headers = new() { ["X-Dormant"] = "stored" } }
        };
        HeaderOverrides.ValidateConfiguration("perEndpoint", common, endpoints);
        Assert.AreSame(custom, HeaderOverrides.Select("perEndpoint", common, endpoints, "/v1/responses?beta=true"));
        Assert.AreSame(common, HeaderOverrides.Select("perEndpoint", common, endpoints, "/v1/messages"));
        Assert.AreSame(common, HeaderOverrides.Select("perEndpoint", common, endpoints, "/v1/models"));
        Assert.AreSame(common, HeaderOverrides.Select("shared", common, endpoints, "/v1/responses"));
        common["X-Common"] = "changed";
        Assert.IsFalse(custom.ContainsKey("X-Common"));
        Assert.AreEqual("changed", HeaderOverrides.Select("perEndpoint", common, endpoints, "/v1/messages")["X-Common"]!.GetValue<string>());
        endpoints["/v1/responses"].Headers = new();
        Assert.AreEqual(0, HeaderOverrides.Select("perEndpoint", common, endpoints, "/v1/responses").Count);
        Assert.IsTrue(new EndpointHeaderOverride().UseCommon);
    }

    [TestMethod]
    [DataRow("legacy", "common")]
    [DataRow("shared", "common")]
    [DataRow("missing", "common")]
    [DataRow("inherit", "common")]
    [DataRow("custom", "endpoint")]
    [DataRow("empty", "legacy")]
    public async Task InvocationUsesSelectedSetAndPreservesHeaderPriority(string scenario, string expected)
    {
        var account = Account();
        Edit(account, settings =>
        {
            settings["extraParams"] = """{"ReplaceHeaders":{"X-Scope":"legacy","Authorization":"Bearer legacy"}}""";
            settings["headerOverride"] = new JsonObject { ["X-Scope"] = "common", ["X-Common-Only"] = "common" };
            if (scenario != "legacy")
            {
                settings["headerOverrideMode"] = scenario == "shared" ? "shared" : "perEndpoint";
                settings["endpointHeaderOverrides"] = scenario == "missing" ? new JsonObject() : new JsonObject
                {
                    ["/v1/responses"] = Entry(scenario == "empty" ? new() : new()
                    {
                        ["X-Scope"] = "endpoint", ["Authorization"] = "Bearer {api_key}",
                        ["X-Client"] = "{client_header:X-Source}", ["re:^X-Pass"] = true
                    }, scenario == "inherit")
                };
            }
        });
        var client = Client(request =>
        {
            Assert.AreEqual(expected, request.Headers.GetValues("X-Scope").Single());
            Assert.AreEqual(scenario is "custom" or "empty", !request.Headers.Contains("X-Common-Only"));
            Assert.AreEqual(scenario == "custom" ? "Bearer endpoint-secret" : "Bearer legacy", request.Headers.Authorization!.ToString());
            if (scenario == "custom")
            {
                Assert.AreEqual("copied", request.Headers.GetValues("X-Client").Single());
                Assert.AreEqual("passed", request.Headers.GetValues("X-Pass-Trace").Single());
            }
        });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.RequestHeaders["X-Source"] = "copied";
        context.Request.RequestHeaders["X-Pass-Trace"] = "passed";
        Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("/v1/responses", "/v1/messages")]
    [DataRow("/v1/messages", "/v1/responses")]
    public async Task ProtocolRoutingSelectsUpstreamHeadersNotIncomingHeaders(string incoming, string target)
    {
        var account = Account();
        Edit(account, settings =>
        {
            settings["headerOverrideMode"] = "perEndpoint";
            settings["modelProtocols"] = JsonSerializer.SerializeToNode(new Dictionary<string, ModelProtocolOptions>
            {
                ["model"] = new() { Protocols = [target[4..]], PreferredProtocol = target[4..] }
            }, Json);
            settings["endpointHeaderOverrides"] = new JsonObject
            {
                ["/v1/responses"] = Entry(new() { ["X-Scope"] = "/v1/responses" }),
                ["/v1/messages"] = Entry(new() { ["X-Scope"] = "/v1/messages" })
            };
        });
        var client = Client(request =>
        {
            Assert.AreEqual(target, request.RequestUri!.AbsolutePath);
            Assert.AreEqual(target, request.Headers.GetValues("X-Scope").Single());
            Assert.AreEqual(target == "/v1/messages" ? "endpoint-secret" : "Bearer endpoint-secret",
                request.Headers.GetValues(target == "/v1/messages" ? "x-api-key" : "Authorization").Single());
            Assert.IsFalse(request.Headers.Contains("X-Selected"));
        });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.Endpoint = incoming;
        Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task SaveRoundTripsAndPreservesOmittedFieldsButReplacesSuppliedMap()
    {
        var account = Account();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var legacy = await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }));
        var old = JsonSerializer.SerializeToNode(legacy.Body, Json)!["accounts"]![0]!;
        Assert.AreEqual("shared", old["headerOverrideMode"]!.GetValue<string>());
        Assert.AreEqual(0, old["endpointHeaderOverrides"]!.AsObject().Count);
        var endpoints = new JsonObject
        {
            ["/v1/responses"] = Entry(new() { ["Originator"] = "codex_exec" }),
            ["/v1/messages"] = Entry(new() { ["X-Dormant"] = "saved" }, true)
        };
        var saved = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        {
            id = account.Id, label = account.Label, headerOverrideMode = "perEndpoint", endpointHeaderOverrides = endpoints
        }));
        Assert.AreEqual(200, saved.StatusCode);
        var card = JsonSerializer.SerializeToNode(saved.Body, Json)!["account"]!;
        Assert.AreEqual("perEndpoint", card["headerOverrideMode"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(endpoints, card["endpointHeaderOverrides"]));

        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, headerOverride = new JsonObject { ["X-Common"] = "new" } }))).StatusCode);
        Assert.AreEqual("perEndpoint", ChannelKeysTests.Settings(account)["headerOverrideMode"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(endpoints, ChannelKeysTests.Settings(account)["endpointHeaderOverrides"]));
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, headerOverrideMode = "shared" }))).StatusCode);
        Assert.IsTrue(JsonNode.DeepEquals(endpoints, ChannelKeysTests.Settings(account)["endpointHeaderOverrides"]));
        var replacement = new JsonObject { ["/v1/responses"] = Entry(new()) };
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, endpointHeaderOverrides = replacement }))).StatusCode);
        Assert.IsTrue(JsonNode.DeepEquals(replacement, ChannelKeysTests.Settings(account)["endpointHeaderOverrides"]));
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, endpointHeaderOverrides = new JsonObject() }))).StatusCode);
        Assert.AreEqual(0, ChannelKeysTests.Settings(account)["endpointHeaderOverrides"]!.AsObject().Count);
    }

    [TestMethod]
    [DataRow("""{"headerOverrideMode":"unknown"}""")]
    [DataRow("""{"headerOverrideMode":false}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/models":{"headers":{}}}}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/messages":null}}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/messages":{"headers":null}}}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/messages":{"headers":[]}}}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/messages":{"useCommon":"false"}}}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/messages":{"headers":{"Host":"forbidden"}}}}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/messages":{"headers":{"X-Test":"bad\r\nvalue"}}}}""")]
    [DataRow("""{"endpointHeaderOverrides":{"/v1/messages":{"headers":{"re:(":"invalid"}}}}""")]
    public async Task InvalidConfigurationIsRejectedBySaveAndInvocation(string invalid)
    {
        var account = Account();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var input = JsonNode.Parse(invalid)!.AsObject();
        input["id"] = account.Id; input["label"] = account.Label;
        var credential = account.Credential;
        Assert.AreEqual(400, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(input))).StatusCode);
        Assert.AreSame(credential, account.Credential);
        Edit(account, settings =>
        {
            foreach (var (key, value) in JsonNode.Parse(invalid)!.AsObject()) settings[key] = value?.DeepClone();
        });
        Assert.IsFalse((await terminal.ValidateCredentialAsync(account.Credential, CancellationToken.None)).Success);
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        Assert.AreEqual(400, (await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, client.Object))).Response.StatusCode);
        Assert.AreEqual(0, client.Invocations.Count);
    }

    [TestMethod]
    public async Task ModelDiscoveryAndRefreshAlwaysUseCommonHeaders()
    {
        var account = Account();
        Edit(account, settings =>
        {
            settings["headerOverrideMode"] = "perEndpoint";
            settings["headerOverride"] = new JsonObject { ["X-Common"] = "stored" };
            settings["endpointHeaderOverrides"] = new JsonObject
            {
                ["/v1/responses"] = Entry(new() { ["X-Endpoint"] = "responses" }),
                ["/v1/messages"] = Entry(new() { ["X-Endpoint"] = "messages" })
            };
        });
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            Assert.AreEqual("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.AreEqual(calls == 1 ? "draft" : "stored", request.Headers.GetValues("X-Common").Single());
            Assert.IsFalse(request.Headers.Contains("X-Endpoint"));
            Assert.AreEqual("Bearer endpoint-secret", request.Headers.Authorization!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"model"}]}""") };
        });
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        {
            id = account.Id, headerOverride = new JsonObject { ["X-Common"] = "draft" }
        }))).StatusCode);
        Assert.AreEqual(200, (await terminal.RefreshModelsAsync(ChannelKeysTests.Context(new { id = account.Id }))).StatusCode);
        Assert.AreEqual(2, calls);
        Assert.AreEqual("perEndpoint", ChannelKeysTests.Settings(account)["headerOverrideMode"]!.GetValue<string>());
        Assert.AreEqual(2, ChannelKeysTests.Settings(account)["endpointHeaderOverrides"]!.AsObject().Count);
    }

    private static Account Account() => ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "endpoint-secret" }]);
    private static JsonObject Entry(JsonObject headers, bool useCommon = false) => new()
    { ["useCommon"] = useCommon, ["headers"] = headers };

    internal static void Edit(Account account, Action<JsonObject> change)
    {
        var settings = ChannelKeysTests.Settings(account);
        change(settings);
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() });
    }

    private static Mock<IPluginHttpClient> Client(Action<HttpRequestMessage> inspect)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) =>
            {
                inspect(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            });
        return client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
