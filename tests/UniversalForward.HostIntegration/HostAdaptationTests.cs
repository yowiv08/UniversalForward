using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Pipeline;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace UniversalForward.HostIntegration;

/// <summary>真实宿主映射、执行器和用量解析；仅 HTTP 上游和持久化账号使用测试替身。</summary>
[TestClass]
public sealed class HostAdaptationTests
{
    [TestMethod]
    [DataRow(429)]
    [DataRow(502)]
    [DataRow(503)]
    public async Task ExhaustedInternalRetriesAllowHostMappingFailover(int status)
    {
        using var session = new Session(call => Json(call.Model == "primary" ? status : 200,
            call.Model == "primary" ? Failure : Success), internalRetries: 2);
        var result = await session.ExecuteAsync();
        Assert.AreEqual(200, result.Response!.StatusCode);
        Assert.AreEqual(3, session.Calls.Count(call => call.Model == "primary"));
        Assert.AreEqual("backup", session.Calls[^1].Model);
        Assert.AreEqual(2, result.AttemptDetails.Count);
        Assert.IsTrue(session.Calls.Take(3).All(call => call.Key == session.Calls[0].Key && call.Body == session.Calls[0].Body));
        Assert.AreEqual(0, session.Account.Status.InFlight);
    }

    [TestMethod]
    [DataRow(1, null, 0, 1)]
    [DataRow(3, null, 0, 3)]
    [DataRow(3, null, 2, 9)]
    [DataRow(3, 1, 2, 3)]
    [DataRow(1, 3, 2, 9)]
    public async Task SavedHostDefaultAndMappingOverridePreserveInternalBudget(int saved, int? mapped, int internalRetries, int sends)
    {
        using var session = new Session(_ => Json(503, Failure), internalRetries: internalRetries,
            hostDefault: saved, mappedAttempts: mapped, backup: false);
        var result = await session.ExecuteAsync();
        Assert.AreEqual(sends, session.Calls.Count);
        Assert.AreEqual(mapped ?? saved, result.AttemptDetails.Count);
        Assert.AreEqual(503, result.Response!.StatusCode);
    }

    [TestMethod]
    public async Task HostAndInternalRetriesReuseOneAppendedSystemPrompt()
    {
        const string original = "Existing client instructions.";
        const string addition = "  渠道附加指令\nKeep the original formatting.  ";
        using var session = new Session(_ => Json(503, Failure), internalRetries: 2,
            hostDefault: 3, backup: false,
            systemPromptPolicy: new() { Mode = "append", Text = addition });
        var result = await session.ExecuteAsync(systemPrompt: original);
        Assert.AreEqual(503, result.Response!.StatusCode);
        Assert.AreEqual(9, session.Calls.Count);
        Assert.AreEqual(3, result.AttemptDetails.Count);
        foreach (var call in session.Calls)
        {
            using var sent = JsonDocument.Parse(call.Body);
            Assert.AreEqual(original + "\n\n" + addition, sent.RootElement.GetProperty("system").GetString());
        }
        Assert.IsTrue(session.Calls.All(call => call.Body == session.Calls[0].Body));
        Assert.AreEqual(original, result.Request.OriginalBody!.Value.GetProperty("system").GetString());
        Assert.AreEqual(0, session.Account.Status.InFlight);
    }

    [TestMethod]
    [DataRow("append")]
    [DataRow("replace")]
    public async Task PromptRewriteSurvivesHostMappingFailover(string mode)
    {
        const string original = "Existing client instructions.";
        const string configured = "Custom channel instructions.";
        using var session = new Session(call => Json(call.Model == "primary" ? 503 : 200,
            call.Model == "primary" ? Failure : Success), internalRetries: 2,
            systemPromptPolicy: new() { Mode = mode, Text = configured });
        var result = await session.ExecuteAsync(systemPrompt: original);
        Assert.AreEqual(200, result.Response!.StatusCode);
        Assert.AreEqual(4, session.Calls.Count);
        Assert.AreEqual("backup", session.Calls[^1].Model);
        foreach (var call in session.Calls)
        {
            using var sent = JsonDocument.Parse(call.Body);
            Assert.AreEqual(mode == "append" ? original + "\n\n" + configured : configured,
                sent.RootElement.GetProperty("system").GetString());
            Assert.AreEqual("Please answer this local test message.",
                sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        }
        Assert.AreEqual(original, result.Request.OriginalBody!.Value.GetProperty("system").GetString());
        Assert.AreEqual(0, session.Account.Status.InFlight);
    }

    [TestMethod]
    public async Task InFlightSequenceRetainsItsSnapshotAndNextRequestUsesSavedValue()
    {
        using var session = new Session(_ => Json(503, Failure), hostDefault: 3, backup: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        session.BeforeSend = async () => { if (first) { first = false; entered.SetResult(); await resume.Task; } };
        var running = session.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.AreEqual(200, (await session.Terminal.SaveExecutionSettingsAsync(new()
            { Body = JsonSerializer.SerializeToElement(new { defaultHostAttempts = 1 }) })).StatusCode);
        }
        finally { resume.SetResult(); }
        Assert.AreEqual(3, (await running).AttemptDetails.Count);
        session.Calls.Clear();
        Assert.AreEqual(1, (await session.ExecuteAsync()).AttemptDetails.Count);
        Assert.AreEqual(1, session.Calls.Count);
    }

    [TestMethod]
    [DataRow(400, Failure)]
    [DataRow(413, Failure)]
    [DataRow(415, Failure)]
    [DataRow(422, Failure)]
    [DataRow(429, "{\"error\":{\"code\":\"insufficient_quota\",\"message\":\"insufficient balance\"}}")]
    public async Task InputAndPermanentErrorsDoNotSwitch(int status, string body)
    {
        using var session = new Session(_ => Json(status, body), hostDefault: 3);
        var result = await session.ExecuteAsync();
        Assert.AreEqual(status, result.Response!.StatusCode);
        Assert.AreEqual(1, session.Calls.Count);
    }

    [TestMethod]
    public async Task ExistingHostCooldownAndStatusMappingStillApply()
    {
        using (var session = new Session(call => Json(call.Model == "primary" ? 503 : 200,
            call.Model == "primary" ? Failure : Success), threshold: 2))
        {
            for (var request = 0; request < 3; request++) Assert.AreEqual(200, (await session.ExecuteAsync()).Response!.StatusCode);
            Assert.AreEqual(2, session.Calls.Count(call => call.Model == "primary"));
            Assert.AreEqual(3, session.Calls.Count(call => call.Model == "backup"));
        }
        using var mapped = new Session(_ => Json(503, Failure), hostDefault: 3, threshold: 1, statusMap: new() { [503] = 200 });
        for (var request = 0; request < 2; request++) Assert.AreEqual(200, (await mapped.ExecuteAsync()).Response!.StatusCode);
        Assert.AreEqual(2, mapped.Calls.Count);
        Assert.IsTrue(mapped.Calls.All(call => call.Model == "primary"));
        Assert.IsTrue(mapped.Logs.All(log => log.Success));
    }

    [TestMethod]
    public async Task CacheUsageMatchesHostAndPartialUsageAllowsEstimation()
    {
        using (var complete = new Session(_ => Json(200, WithUsage("{\"input_tokens\":2,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":4,\"output_tokens\":4}"))))
        {
            var result = await complete.ExecuteAsync();
            Assert.AreEqual(new Usage(9, 4, 13), result.Response!.Usage);
            Assert.AreEqual(new Usage(9, 4, 13), complete.Logs.Single().Usage);
        }
        foreach (var usage in new[] { "{}", "{\"total_tokens\":99}" })
        {
            using var partial = new Session(_ => Json(200, WithUsage(usage)));
            var result = await partial.ExecuteAsync();
            Assert.IsNull(result.Response!.Usage);
            var measured = new RequestUsageTracker(result.Request, result.Response).Finish(true);
            Assert.IsTrue(measured.Usage!.PromptTokens > 0);
            Assert.IsTrue(measured.Usage.CompletionTokens > 0);
            Assert.AreNotEqual("plugin", measured.Metadata.Source);
            if (usage.Contains("99", StringComparison.Ordinal)) Assert.AreEqual(99, measured.Usage.TotalTokens);
        }
    }

    [TestMethod]
    public async Task ReturnedStreamIsNeverReplayedAfterOutputFailure()
    {
        using var session = new Session(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\n", Encoding.UTF8, "text/event-stream") }, hostDefault: 3);
        var result = await session.ExecuteAsync(stream: true);
        Assert.IsNotNull(result.Response!.RawStream);
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in result.Response.RawStream) { } });
        Assert.AreEqual(1, session.Calls.Count);
        if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
    }

    private const string Failure = "{\"error\":{\"type\":\"overloaded_error\",\"message\":\"test upstream unavailable\"}}";
    private const string Success = "{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Local response long enough to estimate output tokens.\"}],\"stop_reason\":\"end_turn\"}";
    private static string WithUsage(string usage) => Success[..^1] + ",\"usage\":" + usage + "}";
    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed record Call(string Model, string Key, string Body);
    private sealed class Handler(Func<Call, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            return await respond(new(json.RootElement.GetProperty("model").GetString()!,
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : request.Headers.Authorization?.Parameter ?? "", body), cancellationToken);
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "universalforward-host-tests", Guid.NewGuid().ToString("N"));
        private readonly Handler _handler;
        private readonly PluginResiliencePipelines _resilience = new();
        private readonly RouterPipeline _pipeline;
        public UniversalForwardTerminal Terminal { get; }
        public Account Account { get; }
        public List<Call> Calls { get; } = [];
        public List<RequestLog> Logs { get; } = [];
        public Func<Task>? BeforeSend { get; set; }

        public Session(Func<Call, HttpResponseMessage> respond, int internalRetries = 0, int hostDefault = 1,
            int? mappedAttempts = null, bool backup = true, int threshold = 0, Dictionary<int, int>? statusMap = null,
            SystemPromptPolicy? systemPromptPolicy = null)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "execution-settings.json"), JsonSerializer.Serialize(new { defaultHostAttempts = hostDefault }));
            _handler = new(async (call, cancellation) => { Calls.Add(call); if (BeforeSend is { } before) await before().WaitAsync(cancellation); return respond(call); });
            Account = new()
            {
                Id = "test-channel", PluginKey = "universalforward", Platform = "universalforward",
                Credential = new CustomCredential(new Dictionary<string, string?>
                {
                    ["settings"] = JsonSerializer.Serialize(new { baseUrl = "https://test.invalid", apiKey = "test-key", systemPromptPolicy = systemPromptPolicy ?? new(), requestPolicy = new ForwardRequestPolicy
                    { MaxRetries = internalRetries, StatusCodeMapping = statusMap ?? [] } }),
                    ["models"] = "[\"primary\",\"backup\"]", ["modelsConfigured"] = "true"
                })
            };
            var pluginAccounts = new Mock<IPluginAccounts>();
            pluginAccounts.Setup(value => value.ListAsync("universalforward", It.IsAny<CancellationToken>())).ReturnsAsync([Account]);
            var log = Mock.Of<IPluginLogSink>();
            var options = Options.Create(new PluginExecutionOptions { SetupTimeout = TimeSpan.FromSeconds(15), TotalSetupTimeout = TimeSpan.FromSeconds(30), ResponseTimeout = TimeSpan.FromSeconds(15) });
            var services = new Mock<IPluginServices>();
            services.SetupGet(value => value.PluginKey).Returns("universalforward");
            services.SetupGet(value => value.Accounts).Returns(pluginAccounts.Object);
            services.SetupGet(value => value.State).Returns(new PluginStateServices(Mock.Of<IPluginStateStore>(), Mock.Of<IPluginStateStore>()));
            services.SetupGet(value => value.Http).Returns(Mock.Of<IPluginHttpServices>());
            services.SetupGet(value => value.Log).Returns(log);
            services.SetupGet(value => value.Execution).Returns(options.Value);
            var policies = new PluginPolicyRegistry();
            var host = new Mock<IPluginHost>();
            host.SetupGet(value => value.Services).Returns(services.Object);
            host.Setup(value => value.GetServiceAsync<IPluginPolicyRegistry>()).ReturnsAsync(policies);
            Terminal = new(host.Object) { SettingsDirectory = _directory };
            var proxyPolicy = new PluginProxyPolicyBuilder();
            var accountPolicy = new PluginAccountPolicyBuilder();
            var builder = new Mock<IPluginBuilder>();
            builder.Setup(value => value.ProxyPolicy(It.IsAny<Action<PluginProxyPolicyBuilder>>())).Callback((Action<PluginProxyPolicyBuilder> configure) => configure(proxyPolicy));
            builder.Setup(value => value.AccountPolicy(It.IsAny<Action<PluginAccountPolicyBuilder>>())).Callback((Action<PluginAccountPolicyBuilder> configure) => configure(accountPolicy));
            Terminal.Configure(builder.Object);
            Terminal.StartAsync(new PluginStartContext("universalforward", host.Object), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            policies.Set("universalforward", proxyPolicy.Build(), accountPolicy.Build());
            policies.Set("universalforward", "universalforward", proxyPolicy.Build(), accountPolicy.Build());
            var accounts = new Mock<IAccountService>();
            accounts.Setup(value => value.GetAsync("universalforward", Account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Account);
            var clients = new Mock<IProxyHttpClientFactory>();
            clients.Setup(value => value.CreateDirectClient("upstream")).Returns(() => new HttpClient(_handler, false));
            var executor = new PluginAttemptExecutor(accounts.Object, Mock.Of<IProxyStore>(), Mock.Of<IProxySubscriptionService>(), new ResourceLeaseManager(),
                Mock.Of<IProxyPolicyStore>(), Mock.Of<ISharedKeyValueStore>(), clients.Object, policies, Mock.Of<IModelMetadataCatalog>(), log,
                NullLogger<PluginAttemptExecutor>.Instance, _resilience, options);
            var platforms = new PlatformRegistry();
            platforms.Register(new("universalforward", "universalforward", "UniversalForward", Terminal));
            var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = Path.Combine(_directory, "mapping.db") }), NullLogger<SqlSugarDatabase>.Instance);
            new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            var health = new ModelMappingHealth();
            var mappings = new ModelMappingStore(database, health);
            var targets = new List<ModelMappingTarget> { new("universalforward", "universalforward", "primary", RetryCount: mappedAttempts, FailureThreshold: threshold) };
            if (backup) targets.Add(new("universalforward", "universalforward", "backup", RetryCount: 1));
            mappings.Save(new("", "test-route", true, targets));
            var requestLogs = new Mock<ILogSink<RequestLog>>();
            requestLogs.Setup(value => value.WriteAsync(It.IsAny<RequestLog>(), It.IsAny<CancellationToken>()))
                .Callback((RequestLog value, CancellationToken _) => Logs.Add(value)).Returns(Task.CompletedTask);
            _pipeline = new(new ModelRouter(platforms), platforms, executor, new RealtimeMetrics(), requestLogs.Object,
                Mock.Of<IUsageBucketStore>(), NullLogger<RouterPipeline>.Instance, health, mappings, executor, options);
        }

        public async Task<RequestContext> ExecuteAsync(bool stream = false, string? systemPrompt = null)
        {
            var context = new RequestContext
            {
                Request = new AdapterRequest { Model = "test-route", Endpoint = "/v1/messages", Stream = stream,
                    OriginalBody = JsonSerializer.SerializeToElement(new { model = "test-route", max_tokens = 32, stream, system = systemPrompt,
                        messages = new[] { new { role = "user", content = "Please answer this local test message." } } }) },
                TestOverrides = new() { AccountId = Account.Id, SkipCooldown = true }
            };
            await _pipeline.ExecuteAsync(context);
            return context;
        }
        public void Dispose()
        {
            Terminal.Dispose(); _resilience.Dispose(); _handler.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }
}
