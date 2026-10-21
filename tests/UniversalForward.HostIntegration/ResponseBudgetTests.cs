using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Infrastructure.Services;

namespace UniversalForward.HostIntegration;

/// <summary>使用真实宿主执行器、物理连接池和本地 HTTP/WS 上游验证插件预算及取消。</summary>
[TestClass]
public sealed class ResponseBudgetTests
{
    private const string Delta = """{"type":"response.output_text.delta","delta":"OK"}""";
    private const string Done = """{"type":"response.completed","response":{"id":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}}""";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ManualSubscriptions = ["manual"];
    private static readonly Version OfficialContractsVersion = new(2, 0, 0, 0);

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task SuccessfulHeadersSwitchTheRealHostBudgetBeforeSlowFirstOutput(
        bool websocket, bool pool, bool streaming)
    {
        Assert.AreEqual(OfficialContractsVersion, typeof(IPluginHost).Assembly.GetName().Version,
            "集成测试必须使用官方 Contracts 2.0 程序集");
        await using var upstream = await Upstream.StartAsync(websocket, slow: true);
        await using var session = new Session(upstream, pool, websocket);
        var result = await session.ExecuteAsync(streaming);
        Assert.AreEqual(200, result.Response.StatusCode, result.Response.Error);
        Assert.AreEqual(1, session.ResponseStartedCount, "插件应仅在成功 SSE 响应建立时通知宿主一次");
        if (streaming)
        {
            var output = await ReadAsync(result.Response.RawStream!);
            StringAssert.Contains(output, Delta);
            StringAssert.Contains(output, Done);
        }
        else
        {
            using var body = JsonDocument.Parse(result.Response.RawContent!);
            Assert.AreEqual("response", body.RootElement.GetProperty("id").GetString());
            Assert.AreEqual(3, body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        }
        Assert.AreEqual(1, upstream.Requests, "完成或超时均不得隐式重放上游请求");
        Assert.AreEqual(0, session.Account.Status.InFlight, "流结束后归还宿主账号租约");
        if (pool)
        {
            var rows = JsonSerializer.SerializeToElement((await session.Terminal.ListRequestLogs(new PluginHttpContext())).Body,
                WebJsonOptions).GetProperty("rows");
            Assert.AreEqual("chosen", rows[0].GetProperty("proxy").GetProperty("id").GetString());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerCancellationDuringAggregationRemainsCancellation(bool websocket)
    {
        await using var upstream = await Upstream.StartAsync(websocket, slow: true);
        await using var session = new Session(upstream, pool: true, websocket);
        using var cancellation = new CancellationTokenSource();
        var execution = session.ExecuteAsync(streaming: false, cancellation.Token);
        await session.ResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await execution);
        Assert.AreEqual(0, session.Account.Status.InFlight);
        Assert.IsTrue(session.Logs.Any(log => log.EventType == "request.cancelled"));
        Assert.IsFalse(session.Logs.Any(log => log.EventType == "request.completed"));
        Assert.AreEqual(1, upstream.Requests);
    }

    [TestMethod]
    public async Task HostResponseDeadlineStillCancelsAnEstablishedStream()
    {
        await using var upstream = await Upstream.StartAsync(websocket: false, slow: true);
        await using var session = new Session(upstream, pool: true, websocket: false,
            responseTimeout: TimeSpan.FromMilliseconds(200));
        var result = await session.ExecuteAsync(streaming: false);
        Assert.IsTrue(session.ResponseStarted.Task.IsCompletedSuccessfully, "必须先进入响应体阶段再触发响应整体时限");
        Assert.IsFalse(result.Response.IsSuccess, "通知响应体阶段不得关闭宿主整体响应预算");
        Assert.AreEqual(0, session.Account.Status.InFlight);
        Assert.AreEqual(1, upstream.Requests);
    }

    private static async Task<string> ReadAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> stream)
    {
        using var bytes = new MemoryStream();
        await foreach (var chunk in stream) await bytes.WriteAsync(chunk);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly ProxyTransportFactory _transport = new();
        private readonly PluginResiliencePipelines _resilience = new();
        private readonly PluginAttemptExecutor _executor;
        private readonly IPlatformTerminal _observedTerminal;
        private int _responseStartedCount;
        public Account Account { get; }
        public UniversalForwardTerminal Terminal { get; }
        public List<PluginLog> Logs { get; } = [];
        public TaskCompletionSource ResponseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResponseStartedCount => Volatile.Read(ref _responseStartedCount);

        public Session(Upstream upstream, bool pool, bool websocket, TimeSpan? responseTimeout = null)
        {
            var address = upstream.Address;
            Account = new Account
            {
                Id = "channel", PluginKey = "universalforward", Platform = "universalforward",
                Credential = new CustomCredential(new Dictionary<string, string?>
                {
                    ["settings"] = JsonSerializer.Serialize(new
                    {
                        baseUrl = pool ? "http://upstream.invalid" : address.GetLeftPart(UriPartial.Authority),
                        apiKey = "local-test-key", networkMode = pool ? "proxyPool" : "direct",
                        proxySubscriptionIds = ManualSubscriptions, proxyId = "chosen",
                        responsesTransport = websocket ? "websocket" : "http",
                        headerOverride = new Dictionary<string, string> { ["Originator"] = "codex_exec" },
                        requestPolicy = new { maxRetries = 0, rateLimitRetryEnabled = false,
                            emptyResponseRetryEnabled = false, headerTimeoutSeconds = 5,
                            totalTimeoutSeconds = 10, streamIdleTimeoutSeconds = 5 }
                    }),
                    ["models"] = "[\"model\"]", ["modelsConfigured"] = "true"
                })
            };
            var nodes = new Mock<IProxyStore>();
            ProxyEndpoint[] proxyNodes = [
                new ProxyEndpoint { Id = "other", SubscriptionId = "manual", Host = "127.0.0.1", Port = 1 },
                new ProxyEndpoint { Id = "chosen", SubscriptionId = "manual", Host = "127.0.0.1", Port = upstream.Proxy.Port }
            ];
            nodes.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(proxyNodes);
            nodes.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => proxyNodes.FirstOrDefault(node => node.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
            var subscriptions = new Mock<IProxySubscriptionService>();
            subscriptions.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var poolFactory = new ProxyPoolHttpClientFactory(nodes.Object, subscriptions.Object, _transport, _resilience);
            var proxyClients = new ProxyHttpClientFactory(_transport);
            var http = new Mock<IPluginHttpServices>();
            http.SetupGet(value => value.Pool).Returns(poolFactory);
            http.Setup(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
                .Returns((PluginHttpClientOptions options) => _transport.CreateClient(null, options));
            var pluginAccounts = new Mock<IPluginAccounts>();
            pluginAccounts.Setup(value => value.ListAsync("universalforward", It.IsAny<CancellationToken>())).ReturnsAsync([Account]);
            var sink = new Mock<IPluginLogSink>();
            sink.Setup(value => value.WriteAsync(It.IsAny<PluginLog>(), It.IsAny<CancellationToken>()))
                .Callback((PluginLog log, CancellationToken _) => Logs.Add(log)).Returns(Task.CompletedTask);
            var execution = new PluginExecutionOptions
            {
                SetupTimeout = TimeSpan.FromMilliseconds(400), TotalSetupTimeout = TimeSpan.FromMilliseconds(500),
                ResponseTimeout = responseTimeout ?? TimeSpan.FromSeconds(5)
            };
            var capabilities = new Mock<IPluginServices>();
            capabilities.SetupGet(value => value.PluginKey).Returns("universalforward");
            capabilities.SetupGet(value => value.Http).Returns(http.Object);
            capabilities.SetupGet(value => value.Accounts).Returns(pluginAccounts.Object);
            capabilities.SetupGet(value => value.State).Returns(new PluginStateServices(Mock.Of<IPluginStateStore>(), Mock.Of<IPluginStateStore>()));
            capabilities.SetupGet(value => value.Log).Returns(sink.Object);
            capabilities.SetupGet(value => value.Execution).Returns(execution);
            var host = new Mock<IPluginHost>();
            host.SetupGet(value => value.Services).Returns(capabilities.Object);
            host.Setup(value => value.GetServiceAsync<IProxyStore>()).ReturnsAsync(nodes.Object);
            host.Setup(value => value.GetServiceAsync<IProxySubscriptionService>()).ReturnsAsync(subscriptions.Object);
            host.Setup(value => value.GetServiceAsync<IProxyHttpClientFactory>()).ReturnsAsync(proxyClients);
            Terminal = new UniversalForwardTerminal(host.Object);
            _observedTerminal = new ObservedTerminal(Terminal, () =>
            {
                Interlocked.Increment(ref _responseStartedCount);
                ResponseStarted.TrySetResult();
            });
            Terminal.StartAsync(new PluginStartContext("universalforward", host.Object), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var policies = new PluginPolicyRegistry();
            var builder = new Mock<IPluginBuilder>();
            builder.Setup(value => value.ProxyPolicy(It.IsAny<Action<PluginProxyPolicyBuilder>>()))
                .Callback((Action<PluginProxyPolicyBuilder> configure) =>
                {
                    var policy = new PluginProxyPolicyBuilder(); configure(policy);
                    policies.Set("universalforward", policy.Build());
                });
            Terminal.Configure(builder.Object);
            var accounts = new Mock<IAccountService>();
            accounts.Setup(value => value.GetAsync("universalforward", Account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Account);
            _executor = new PluginAttemptExecutor(accounts.Object, nodes.Object, subscriptions.Object,
                new ResourceLeaseManager(), Mock.Of<IProxyPolicyStore>(), Mock.Of<ISharedKeyValueStore>(),
                proxyClients, policies, Mock.Of<IModelMetadataCatalog>(), sink.Object,
                NullLogger<PluginAttemptExecutor>.Instance, _resilience, Options.Create(execution));
        }

        public Task<PluginInvocationResult> ExecuteAsync(bool streaming, CancellationToken cancellation = default)
            => _executor.ExecuteAsync("universalforward", "universalforward", _observedTerminal, new AdapterRequest
            {
                Model = "model", Endpoint = "/v1/responses", Stream = streaming,
                OriginalBody = JsonSerializer.SerializeToElement(new { model = "model", input = "hello", stream = streaming })
            }, "local-integration", new TestOverrides { AccountId = Account.Id, SkipCooldown = true }, cancellation);

        public ValueTask DisposeAsync()
        {
            Terminal.Dispose(); _transport.Dispose(); _resilience.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>只观察插件调用真实宿主通知的时刻，不替代执行器或预算逻辑。</summary>
    private sealed class ObservedTerminal(IPlatformTerminal terminal, Action responseStarted) : IPlatformTerminal
    {
        public Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
            => terminal.InvokeAsync(new PluginAttemptContext
            {
                Request = context.Request, PlatformName = context.PlatformName, PluginKey = context.PluginKey,
                Account = context.Account, HttpClient = context.HttpClient, CancellationToken = context.CancellationToken,
                TraceId = context.TraceId,
                NotifyUpstreamResponseStarted = () =>
                {
                    context.NotifyUpstreamResponseStarted?.Invoke();
                    responseStarted();
                }
            });

        public Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken cancellationToken)
            => terminal.GetModelsAsync(context, cancellationToken);

        public Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken cancellationToken)
            => terminal.ValidateCredentialAsync(credential, cancellationToken);
    }

    private sealed class Upstream(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public LoopbackProxy Proxy { get; } = new(address.Port);
        public int Requests { get; private set; }
        public TaskCompletionSource HeadersSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Upstream> StartAsync(bool websocket, bool slow)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            Upstream? server = null;
            app.Run(async context =>
            {
                server!.Requests++;
                try
                {
                    var token = context.RequestAborted;
                    if (websocket)
                    {
                        using var socket = await context.WebSockets.AcceptWebSocketAsync();
                        var payload = new byte[16384];
                        await socket.ReceiveAsync(payload.AsMemory(), token);
                        server.HeadersSent.TrySetResult();
                        if (slow) await Task.Delay(800, token);
                        await socket.SendAsync(Encoding.UTF8.GetBytes(Delta).AsMemory(), WebSocketMessageType.Text, true, token);
                        await socket.SendAsync(Encoding.UTF8.GetBytes(Done).AsMemory(), WebSocketMessageType.Text, true, token);
                    }
                    else
                    {
                        await context.Request.Body.CopyToAsync(Stream.Null, token);
                        context.Response.ContentType = "text/event-stream";
                        await context.Response.StartAsync(token);
                        await context.Response.Body.FlushAsync(token);
                        server.HeadersSent.TrySetResult();
                        if (slow) await Task.Delay(800, token);
                        await context.Response.WriteAsync($"data: {Delta}\n\ndata: {Done}\n\n", token);
                        await context.Response.Body.FlushAsync(token);
                    }
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (WebSocketException) when (context.RequestAborted.IsCancellationRequested) { }
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server = new Upstream(app, new Uri(address));
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await Proxy.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
