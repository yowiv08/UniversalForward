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
public sealed class ProxySelectionTests
{
    private static readonly ProxyPoolNode Selected = new("node", "manual", "http://127.0.0.1:1080", "Active", true);
    private static readonly string[] ManualScope = ["manual"];
    private static readonly string[] DuplicateManual = ["manual", " MANUAL "];
    private static readonly string[] InvalidScope = [" "];
    private static readonly string[] SubscriptionScope = ["subscription"];
    private static readonly string[] Models = ["model"];
    private const string Done = "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"result\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]}}\n\n";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SavePreservesOmittedScopeAndExplicitlyClearsIt(bool clearWithNull)
    {
        var account = Account();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var saved = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        {
            id = account.Id, label = "Channel", proxySubscriptionIds = DuplicateManual, proxyId = " node "
        }));
        Assert.AreEqual(200, saved.StatusCode);
        var settings = ChannelKeysTests.Settings(account);
        Assert.AreEqual("node", settings["proxyId"]!.GetValue<string>());
        Assert.AreEqual(1, settings["proxySubscriptionIds"]!.AsArray().Count);
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = "Channel" }))).StatusCode);
        Assert.AreEqual("node", ChannelKeysTests.Settings(account)["proxyId"]!.GetValue<string>());
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = "Channel", proxySubscriptionIds = clearWithNull ? null : Array.Empty<string>(), proxyId = clearWithNull ? null : "" }))).StatusCode);
        settings = ChannelKeysTests.Settings(account);
        Assert.IsNull(settings["proxyId"]);
        Assert.AreEqual(0, settings["proxySubscriptionIds"]!.AsArray().Count);
        var card = JsonSerializer.SerializeToElement(saved.Body);
        Assert.AreEqual("node", card.GetProperty("account").GetProperty("proxyId").GetString());
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("node")]
    [DataRow("count")]
    public async Task InvalidScopeDoesNotWriteAccount(string scenario)
    {
        var account = Account();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var before = ((CustomCredential)account.Credential).Fields["settings"];
        var result = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        {
            id = account.Id, label = "Channel",
            proxySubscriptionIds = scenario == "source" ? InvalidScope : scenario == "count" ? Enumerable.Repeat("manual", 101).ToArray() : ManualScope,
            proxyId = scenario == "node" ? new string('a', 257) : "node"
        }));
        Assert.AreEqual(400, result.StatusCode);
        Assert.AreEqual(before, ((CustomCredential)account.Credential).Fields["settings"]);
    }

    [TestMethod]
    [DataRow("forward")]
    [DataRow("discover")]
    [DataRow("refresh")]
    [DataRow("test")]
    public async Task ScopeAndSelectedNodeReachEveryOperationAndJournal(string operation)
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        var options = new List<ProxyEndpoint>();
        var pool = Pool(host, () => new HttpClient(new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(operation is "discover" or "refresh"
                ? "{\"data\":[{\"id\":\"model\"}]}"
                : "{\"status\":\"completed\",\"output\":[{\"type\":\"message\"}]}")
        })), options);
        using var logs = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(host) { RequestLogs = logs };
        if (operation == "forward")
        {
            var result = await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>()));
            Assert.AreEqual(200, result.Response.StatusCode);
        }
        else if (operation == "test")
        {
            PluginJobRegistration? registration = null;
            var builder = new Mock<IPluginBuilder>();
            builder.Setup(x => x.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(r => registration = r);
            terminal.Configure(builder.Object);
            var result = await registration!.ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
                JsonSerializer.SerializeToElement(new { accountId = account.Id, models = Models }), _ => { }, CancellationToken.None));
            Assert.AreEqual("node", result!.Value.GetProperty("rows")[0].GetProperty("proxy").GetProperty("id").GetString());
            Assert.IsTrue(result.Value.GetProperty("rows")[0].GetProperty("success").GetBoolean());
        }
        else
        {
            var context = ChannelKeysTests.Context(new { id = account.Id });
            var result = operation == "discover" ? await terminal.DiscoverModelsAsync(context) : await terminal.RefreshModelsAsync(context);
            Assert.AreEqual(200, result.StatusCode);
        }
        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("node", options[0].Id);
        Assert.AreEqual("manual", options[0].SubscriptionId);
        await logs.FlushAsync();
        var row = JsonSerializer.SerializeToElement(logs.List(new Dictionary<string, string>())).GetProperty("rows")[0];
        Assert.AreEqual("node", row.GetProperty("proxy").GetProperty("id").GetString());
        var detail = logs.Detail(row.GetProperty("id").GetString()!)!;
        Assert.AreEqual(Selected.Address, detail["proxy"]!["address"]!.ToString());
        Assert.AreEqual("node", detail["attempts"]![0]!["proxy"]!["id"]!.ToString());
        pool.Verify(x => x.CreateClient(It.IsAny<ProxyEndpoint>(), "universalforward"), Times.Once);
    }

    [TestMethod]
    public async Task DiscoveryUsesDraftScopeWithoutOverwritingSavedSelection()
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        var options = new List<ProxyEndpoint>();
        Pool(host, () => new HttpClient(new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[{\"id\":\"model\"}]}") })), options);
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        { id = account.Id, proxySubscriptionIds = SubscriptionScope, proxyId = "draft-node" }))).StatusCode);
        Assert.AreEqual("draft-node", options[0].Id);
        Assert.AreEqual("subscription", options[0].SubscriptionId);
        Assert.AreEqual("node", ChannelKeysTests.Settings(account)["proxyId"]!.ToString());
    }

    [TestMethod]
    public async Task DiscoveryCanClearSavedNodeAndRetainOnlyItsDraftSource()
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        var options = new List<ProxyEndpoint>();
        Pool(host, () => new HttpClient(new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[{\"id\":\"model\"}]}") })), options);
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        { id = account.Id, proxySubscriptionIds = SubscriptionScope, proxyId = (string?)null }))).StatusCode);
        Assert.AreEqual("draft-node", options[0].Id);
        Assert.AreEqual("subscription", options[0].SubscriptionId);
        Assert.AreEqual("node", ChannelKeysTests.Settings(account)["proxyId"]!.ToString());
    }

    [TestMethod]
    public async Task FixedNodeAndItsJournalMetadataAreReusedAcrossRetries()
    {
        var account = Account(retries: 1);
        var host = ChannelKeysTests.Host(account);
        var options = new List<ProxyEndpoint>();
        var sends = 0;
        Pool(host, () => new HttpClient(new ReplyHandler(() => new HttpResponseMessage(
            ++sends == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
        { Content = new StringContent("{\"ok\":true}") })), options);
        using var logs = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(host) { RequestLogs = logs };
        var result = await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>()));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(2, sends);
        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("node", options[0].Id);
        Assert.AreEqual("manual", options[0].SubscriptionId);
        await logs.FlushAsync();
        var row = JsonSerializer.SerializeToElement(logs.List(new Dictionary<string, string>())).GetProperty("rows")[0];
        var attempts = logs.Detail(row.GetProperty("id").GetString()!)!["attempts"]!.AsArray();
        Assert.AreEqual(2, attempts.Count);
        foreach (var attempt in attempts) Assert.AreEqual("node", attempt!["proxy"]!["id"]!.ToString());
    }

    [TestMethod]
    public async Task DirectModeRetainsSavedScopeButDoesNotUsePool()
    {
        var account = Account("direct");
        var host = ChannelKeysTests.Host(account);
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, client.Object))).Response.StatusCode);
        Mock.Get(host).Verify(x => x.GetServiceAsync<IProxyStore>(), Times.Never);
        Assert.AreEqual("node", ChannelKeysTests.Settings(account)["proxyId"]!.ToString());
    }

    [TestMethod]
    public async Task CatalogExposesBudgetAndReportsUnsupportedHost()
    {
        var host = PluginTestHost.Create("universalforward");
        PluginTestHost.ConfigureProxyServices(host, proxies:
        [new ProxyEndpoint { Id = "node", SubscriptionId = "subscription", Host = "::1", Port = 1080, Username = "private-user", Password = "private-password" }],
        subscriptions: [new ProxySubscription { Id = "subscription", Name = "禁用订阅", Enabled = false, Url = "https://subscription.example/private-token" }]);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.ListNetworkProxiesAsync(ChannelKeysTests.Context(new { }));
        Assert.AreEqual(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body, RequestLogStore.Json);
        Assert.AreEqual("node", json.GetProperty("nodes")[0].GetProperty("id").GetString());
        Assert.AreEqual("http://[::1]:1080", json.GetProperty("nodes")[0].GetProperty("address").GetString());
        Assert.IsFalse(json.GetProperty("nodes")[0].GetProperty("available").GetBoolean());
        Assert.IsFalse(json.GetRawText().Contains("private-", StringComparison.Ordinal));
        Assert.AreEqual(180, json.GetProperty("execution").GetProperty("totalSetupTimeoutSeconds").GetDouble());
        using var unsupported = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        result = await unsupported.ListNetworkProxiesAsync(ChannelKeysTests.Context(new { }));
        Assert.AreEqual(503, result.StatusCode);
        StringAssert.Contains(JsonSerializer.Serialize(result.Body), "proxy_selection_not_supported");
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("disabled")]
    [DataRow("cooldown")]
    [DataRow("port")]
    [DataRow("host")]
    [DataRow("node")]
    public async Task UnavailableFixedNodeNeverUsesAnotherNodeOrDirect(string reason)
    {
        var account = Account();
        Update(account, settings => settings["proxySubscriptionIds"] = new JsonArray("subscription"));
        var host = ChannelKeysTests.Host(account);
        var configured = new ProxyEndpoint
        {
            Id = "node", SubscriptionId = reason == "source" ? "manual" : "subscription",
            Host = reason == "host" ? "" : "127.0.0.1", Port = reason == "port" ? 0 : 1080,
            Status = new ResourceStatus { CooldownUntil = reason == "cooldown" ? DateTimeOffset.UtcNow.AddHours(1) : null }
        };
        if (reason == "node") configured.Id = "different-node";
        var clients = PluginTestHost.ConfigureProxyServices(host, proxies:
        [configured, new ProxyEndpoint { Id = "other", SubscriptionId = "subscription", Host = "127.0.0.1", Port = 1081 }],
        subscriptions: [new ProxySubscription { Id = "subscription", Enabled = reason != "disabled" }]);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>(MockBehavior.Strict)));
        Assert.AreEqual(503, result.Response.StatusCode);
        clients.Verify(x => x.CreateClient(It.IsAny<ProxyEndpoint>(), It.IsAny<string>()), Times.Never);
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    [DataRow("forward")]
    [DataRow("discover")]
    [DataRow("refresh")]
    public async Task MissingPublicServicesFailExplicitlyWithoutPoolFallback(string operation)
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        if (operation == "forward")
        {
            var result = await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>(MockBehavior.Strict)));
            Assert.AreEqual(503, result.Response.StatusCode);
            StringAssert.Contains(Encoding.UTF8.GetString(result.Response.RawContent!), "proxy_selection_not_supported");
        }
        else
        {
            var context = ChannelKeysTests.Context(new { id = account.Id });
            var result = operation == "discover" ? await terminal.DiscoverModelsAsync(context) : await terminal.RefreshModelsAsync(context);
            Assert.AreEqual(503, result.StatusCode);
            StringAssert.Contains(JsonSerializer.Serialize(result.Body), "proxy_selection_not_supported");
        }
        Mock.Get(host.Services.Http.Pool).VerifyNoOtherCalls();
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ModelsFollowOnlySameOriginRedirects(bool crossOrigin, bool direct)
    {
        var account = Account(direct ? "direct" : "proxyPool");
        var host = ChannelKeysTests.Host(account);
        var sends = 0;
        HttpClient Create() => new(new RouteHandler(request =>
        {
            Assert.AreEqual("secret", request.Headers.Authorization?.Parameter);
            if (++sends == 1)
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = crossOrigin ? new Uri("https://other.example/models") : new Uri("/redirected/models", UriKind.Relative) } };
            Assert.AreEqual("/redirected/models", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"model\"}]}") };
        }));
        if (direct)
            Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.Is<PluginHttpClientOptions>(options => !options.AllowAutoRedirect))).Returns(Create);
        else Pool(host, Create);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { id = account.Id }));
        Assert.AreEqual(crossOrigin ? 502 : 200, result.StatusCode);
        Assert.AreEqual(crossOrigin ? 1 : 2, sends);
    }

    [TestMethod]
    public async Task ModelsStopAfterFiveRedirects()
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        var sends = 0;
        Pool(host, () => new HttpClient(new ReplyHandler(() =>
        {
            sends++;
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/loop", UriKind.Relative) } };
        })));
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(502, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { id = account.Id }))).StatusCode);
        Assert.AreEqual(6, sends);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ProxySseNotifiesHostBeforeProbeOrNonstreamCollection(bool streaming)
    {
        var account = Account();
        Update(account, s => s["headerOverride"] = JsonNode.Parse("{\"Originator\":\"codex_exec\"}"));
        var host = ChannelKeysTests.Host(account);
        var started = 0;
        Pool(host, () => new HttpClient(new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CheckingStream(Done, () => Assert.AreEqual(1, started)))
            { Headers = { ContentType = new("text/event-stream") } }
        })));
        using var terminal = new UniversalForwardTerminal(host);
        var attempt = Attempt(account, () => started++);
        attempt.Request.Stream = streaming;
        var result = await terminal.InvokeAsync(attempt);
        Assert.AreEqual(200, result.Response.StatusCode);
        if (streaming)
        {
            await foreach (var _ in result.Response.RawStream!) { }
            await result.Response.Lifetime!.DisposeAsync();
        }
        else Assert.AreEqual("result", JsonNode.Parse(result.Response.RawContent!)!["id"]!.ToString());
        Assert.AreEqual(1, started);
    }

    [TestMethod]
    public async Task FailedSseDoesNotNotifyResponseStarted()
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        Pool(host, () => new HttpClient(new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("data: {\"type\":\"error\"}\n\n", Encoding.UTF8, "text/event-stream") })));
        var started = 0;
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(400, (await terminal.InvokeAsync(Attempt(account, () => started++))).Response.StatusCode);
        Assert.AreEqual(0, started);
    }

    [TestMethod]
    public async Task WebSocketUsesScopeAndNotifiesBeforeCollection()
    {
        const string done = "{\"type\":\"response.completed\",\"response\":{\"id\":\"ws\",\"status\":\"completed\",\"output\":[]}}";
        await using var server = new WebSocketLoopback(async (peer, _, token) =>
        {
            using var socket = await peer.AcceptAsync(token);
            await WebSocketLoopback.ReceiveAsync(socket, token);
            await WebSocketLoopback.SendAsync(socket, done, token);
        });
        var account = Account();
        Update(account, s => { s["baseUrl"] = server.Address.GetLeftPart(UriPartial.Authority); s["responsesTransport"] = "websocket"; });
        var host = ChannelKeysTests.Host(account);
        var options = new List<ProxyEndpoint>();
        Pool(host, () => server.CreateClient(), options);
        var started = 0;
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.InvokeAsync(Attempt(account, () => started++));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual("ws", JsonNode.Parse(result.Response.RawContent!)!["id"]!.ToString());
        Assert.AreEqual(1, started);
        Assert.AreEqual("node", options[0].Id);
        await server.Completion;
    }

    [TestMethod]
    [TestCategory("Performance")]
    public async Task LargeRawStreamKeepsContentAndDoesNotAllocateAByteArrayPerChunk()
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        var body = Encoding.UTF8.GetBytes("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\""
            + new string('x', 1024 * 1024) + "\"}]}]}");
        Pool(host, () => new HttpClient(new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(body) })));
        using var terminal = new UniversalForwardTerminal(host);
        var context = ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>());
        context.Request.Stream = true;
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        using var output = new MemoryStream(body.Length);
        // 仅测响应已建立后的原始流读取；内存输入同步完成，不跨线程统计。
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        await foreach (var chunk in result.Response.RawStream!)
        {
            Assert.AreEqual(thread, Environment.CurrentManagedThreadId);
            output.Write(chunk.Span);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await result.Response.Lifetime!.DisposeAsync();
        CollectionAssert.AreEqual(body, output.ToArray());
        Assert.IsTrue(allocated < body.Length / 2,
            $"读取 1 MiB 原始流分配 {allocated} B，不应再为每块复制约 1 MiB 正文。");
    }

    private static Account Account(string mode = "proxyPool", int retries = 0)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "secret" }], retries);
        Update(account, s => { s["networkMode"] = mode; s["proxySubscriptionIds"] = new JsonArray("manual"); s["proxyId"] = "node"; });
        return account;
    }

    private static void Update(Account account, Action<JsonObject> update)
    {
        var settings = ChannelKeysTests.Settings(account);
        update(settings);
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() });
    }

    private static Mock<IProxyHttpClientFactory> Pool(IPluginHost host, Func<HttpClient> create, List<ProxyEndpoint>? observed = null)
    {
        return PluginTestHost.ConfigureProxyServices(host, proxy =>
            {
                observed?.Add(proxy);
                return create();
            }, proxies:
            [
                new ProxyEndpoint { Id = "node", SubscriptionId = "manual", Host = "127.0.0.1", Port = 1080 },
                new ProxyEndpoint { Id = "draft-node", SubscriptionId = "subscription", Host = "127.0.0.1", Port = 1081 }
            ], subscriptions: [new ProxySubscription { Id = "subscription", Name = "订阅" }]);
    }

    private static PluginAttemptContext Attempt(Account account, Action started)
    {
        var original = ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>());
        return new PluginAttemptContext
        {
            PluginKey = original.PluginKey, PlatformName = original.PlatformName, Account = account,
            HttpClient = original.HttpClient, Request = original.Request, NotifyUpstreamResponseStarted = started, CancellationToken = CancellationToken.None
        };
    }

    private sealed class ReplyHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply());
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }

    private sealed class CheckingStream(string body, Action check) : MemoryStream(Encoding.UTF8.GetBytes(body))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            check();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
