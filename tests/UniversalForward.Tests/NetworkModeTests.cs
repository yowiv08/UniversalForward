using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class NetworkModeTests
{
    [TestMethod]
    public async Task ConfigurationDefaultsPersistsAndRejectsInvalidModes()
    {
        var account = Account();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var old = await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }));
        StringAssert.Contains(JsonSerializer.Serialize(old.Body), "\"networkMode\":\"direct\"");
        Assert.AreEqual(400, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = "Channel", networkMode = "invalid" }))).StatusCode);
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = "Channel", networkMode = "proxyPool" }))).StatusCode);
        Assert.AreEqual("proxyPool", ChannelKeysTests.Settings(account)["networkMode"]!.GetValue<string>());
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = "Renamed" }))).StatusCode);
        Assert.AreEqual("proxyPool", ChannelKeysTests.Settings(account)["networkMode"]!.GetValue<string>());
        Assert.AreEqual(400, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        { baseUrl = "https://upstream.example", apiKey = "secret", networkMode = "invalid" }))).StatusCode);
    }

    [TestMethod]
    [DataRow("/v1/responses")]
    [DataRow("/v1/messages")]
    public async Task ProxyInvocationReusesOneClientAndIdentityAcrossRetries(string endpoint)
    {
        var account = Account("proxyPool", 1);
        var host = ChannelKeysTests.Host(account);
        var identities = new List<string>();
        var sends = 0;
        var handler = new Handler(request =>
        {
            Assert.AreEqual(endpoint, request.RequestUri!.AbsolutePath);
            identities.Add(request.Headers.GetValues("X-Identity").Single());
            sends++;
            return Reply(sends == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK, "{\"ok\":true}");
        });
        ConfigurePool(host, handler);
        using var terminal = new UniversalForwardTerminal(host);
        var direct = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        var context = ChannelKeysTests.Attempt(account, direct.Object);
        context.Request.Endpoint = endpoint;
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(2, sends);
        Assert.AreEqual(identities[0], identities[1]);
        Assert.IsTrue(handler.Disposed);
        VerifyPool(host, 1);
        Assert.AreEqual(0, direct.Invocations.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StreamOwnsProxyUntilDisposed(bool consume)
    {
        var account = Account("proxyPool");
        var host = ChannelKeysTests.Host(account);
        const string body = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: {\"type\":\"response.completed\"}\n\n";
        var handler = new Handler(_ => Reply(HttpStatusCode.OK, body, "text/event-stream"));
        ConfigurePool(host, handler);
        using var terminal = new UniversalForwardTerminal(host);
        var context = ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>());
        context.Request.Stream = true;
        var result = await terminal.InvokeAsync(context);
        Assert.IsFalse(handler.Disposed);
        Assert.IsNotNull(result.Response.Lifetime);
        if (consume)
        {
            var text = new StringBuilder();
            await foreach (var chunk in result.Response.RawStream!) text.Append(Encoding.UTF8.GetString(chunk.Span));
            Assert.AreEqual(body, text.ToString());
        }
        await result.Response.Lifetime.DisposeAsync();
        await result.Response.Lifetime.DisposeAsync();
        Assert.IsTrue(handler.Disposed);
    }

    [TestMethod]
    public async Task StreamFailureAfterOutputDisposesProxyWithoutReplay()
    {
        var account = Account("proxyPool", 3);
        var host = ChannelKeysTests.Host(account);
        var sends = 0;
        var handler = new Handler(_ =>
        {
            sends++;
            var content = new StreamContent(new BrokenStream());
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        });
        ConfigurePool(host, handler);
        using var terminal = new UniversalForwardTerminal(host);
        var context = ChannelKeysTests.Attempt(account, Mock.Of<IPluginHttpClient>());
        context.Request.Stream = true;
        var result = await terminal.InvokeAsync(context);
        var output = new StringBuilder();
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await foreach (var chunk in result.Response.RawStream!) output.Append(Encoding.UTF8.GetString(chunk.Span));
        });
        StringAssert.Contains(output.ToString(), "OK");
        Assert.AreEqual(1, sends);
        Assert.IsTrue(handler.Disposed);
        VerifyPool(host, 1);
    }

    [TestMethod]
    public async Task EmptyPoolFailsAllOperationsWithoutDirectFallback()
    {
        var account = Account("proxyPool");
        var host = ChannelKeysTests.Host(account);
        var pool = new Mock<IProxyPoolHttpClientFactory>(MockBehavior.Strict);
        pool.Setup(x => x.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProxyPoolUnavailableException());
        Mock.Get(host.Services.Http).SetupGet(x => x.Pool).Returns(pool.Object);
        using var terminal = new UniversalForwardTerminal(host);
        var direct = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        var invoke = await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, direct.Object));
        Assert.AreEqual(503, invoke.Response.StatusCode);
        StringAssert.Contains(Encoding.UTF8.GetString(invoke.Response.RawContent!), "proxy_pool_unavailable");
        var discover = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { id = account.Id }));
        var refresh = await terminal.RefreshModelsAsync(ChannelKeysTests.Context(new { id = account.Id }));
        foreach (var result in new[] { discover, refresh })
        {
            Assert.AreEqual(503, result.StatusCode);
            StringAssert.Contains(JsonSerializer.Serialize(result.Body), "proxy_pool_unavailable");
        }
        Assert.AreEqual(0, direct.Invocations.Count);
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        VerifyPool(host, 3);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ModelsRespectDraftAndSavedMode(bool refresh)
    {
        var account = Account(refresh ? "proxyPool" : "direct");
        var host = ChannelKeysTests.Host(account);
        var handler = new Handler(request =>
        {
            Assert.AreEqual("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.IsTrue(request.Headers.Contains("X-Identity"));
            return Reply(HttpStatusCode.OK, "{\"data\":[{\"id\":\"model\"}]}");
        });
        ConfigurePool(host, handler);
        using var terminal = new UniversalForwardTerminal(host);
        var result = refresh
            ? await terminal.RefreshModelsAsync(ChannelKeysTests.Context(new { id = account.Id }))
            : await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { id = account.Id, networkMode = "proxyPool" }));
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsTrue(handler.Disposed);
        VerifyPool(host, 1);
        Assert.AreEqual(refresh ? "proxyPool" : "direct", ChannelKeysTests.Settings(account)["networkMode"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DiscoveryActuallyTraversesLocalProxy()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endpoint = (IPEndPoint)proxy.LocalEndpoint;
        var observed = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var first = await reader.ReadLineAsync(timeout.Token);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            var body = "{\"data\":[{\"id\":\"through-proxy\"}]}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), timeout.Token);
            return first;
        }, timeout.Token);
        var host = PluginTestHost.Create("universalforward");
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        pool.Setup(x => x.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpClient(new HttpClientHandler
            {
                UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{endpoint.Port}")
            }));
        Mock.Get(host.Services.Http).SetupGet(x => x.Pool).Returns(pool.Object);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        { baseUrl = "http://upstream.invalid", apiKey = "test", networkMode = "proxyPool" }));
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual("GET http://upstream.invalid/v1/models HTTP/1.1", await observed);
        StringAssert.Contains(JsonSerializer.Serialize(result.Body), "through-proxy");
    }

    private static Account Account(string mode = "direct", int retries = 0)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-secret" }], retries);
        var settings = ChannelKeysTests.Settings(account);
        settings["networkMode"] = mode;
        settings["headerOverride"] = new JsonObject { ["X-Identity"] = "{session_id}" };
        var fields = new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() };
        account.Credential = new CustomCredential(fields);
        return account;
    }

    private static void ConfigurePool(IPluginHost host, Handler handler)
    {
        var pool = new Mock<IProxyPoolHttpClientFactory>(MockBehavior.Strict);
        pool.Setup(x => x.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpClient(handler));
        Mock.Get(host.Services.Http).SetupGet(x => x.Pool).Returns(pool.Object);
    }

    private static void VerifyPool(IPluginHost host, int times)
        => Mock.Get(host.Services.Http.Pool).Verify(x => x.CreateClientAsync(
            It.Is<ProxyPoolHttpClientOptions>(o => !o.AllowDirectFallback && o.SubscriptionIds == null),
            It.IsAny<CancellationToken>()), Times.Exactly(times));

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, string type = "application/json")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class BrokenStream() : MemoryStream(Encoding.UTF8.GetBytes(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\n"))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Position == Length ? ValueTask.FromException<int>(new IOException("connection interrupted")) : base.ReadAsync(buffer, cancellationToken);
    }
}
