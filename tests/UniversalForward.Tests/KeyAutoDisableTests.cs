using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

[TestClass]
public sealed class KeyAutoDisableTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] RepeatedModels = ["model", "model"];
    private static readonly string[] AlphaKey = ["a"];
    private static List<ChannelKey> Keys() => [new() { Id = "a", Name = "Alpha", Secret = "secret-a" }, new() { Id = "b", Name = "Beta", Secret = "secret-b" }];
    private static Account Account(int threshold = 3, int retries = 1, bool enabled = true)
    {
        var account = ChannelKeysTests.Account(Keys(), retries);
        Edit(account, s => s["keyAutoDisable"] = JsonSerializer.SerializeToNode(new KeyAutoDisablePolicy { Enabled = enabled, FailureThreshold = threshold }, Json));
        return account;
    }
    internal static void Edit(Account account, Action<JsonObject> edit)
    {
        var settings = ChannelKeysTests.Settings(account); edit(settings);
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields) { ["settings"] = settings.ToJsonString() });
    }
    private static ChannelKey[] ReadKeys(Account account) => ChannelKeysTests.Settings(account)["keys"]!.Deserialize<ChannelKey[]>(Json)!;
    private static IPluginHttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
    {
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage r, bool _, HttpCompletionOption _, CancellationToken _) => reply(r));
        return client.Object;
    }
    private static HttpResponseMessage Response(int status, string endpoint = "/v1/responses") => new((HttpStatusCode)status)
    { Content = new StringContent(status == 200 ? endpoint == "/v1/messages" ? "{\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}"
        : endpoint == "/v1/chat/completions" ? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}"
        : "{\"status\":\"completed\",\"output\":[{\"type\":\"message\"}]}" : "{\"error\":{\"message\":\"denied\"}}") };
    private static PluginAttemptContext Attempt(Account account, IPluginHttpClient client, string trace = "request", string endpoint = "/v1/responses") => new()
    {
        PluginKey = "universalforward", PlatformName = "universalforward", Account = account, HttpClient = client, TraceId = trace,
        CancellationToken = CancellationToken.None,
        Request = new AdapterRequest { Model = "model", Endpoint = endpoint, OriginalBody = JsonSerializer.SerializeToElement(new { model = "model", input = "OK" }) }
    };
    private static PluginJobRegistration Registration(UniversalForwardTerminal terminal)
    {
        PluginJobRegistration? registration = null;
        var builder = new Mock<IPluginBuilder>();
        builder.Setup(x => x.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(r => registration = r);
        terminal.Configure(builder.Object); return registration!;
    }

    [TestMethod]
    [DataRow("401,403", 3, true)]
    [DataRow("400-599", 100, true)]
    [DataRow("200", 3, false)]
    [DataRow("600", 3, false)]
    [DataRow("403-401", 3, false)]
    [DataRow("", 3, false)]
    [DataRow("401", 0, false)]
    [DataRow("401", 101, false)]
    public void ValidatesPolicy(string codes, int threshold, bool valid)
    {
        var policy = new KeyAutoDisablePolicy { StatusCodes = codes, FailureThreshold = threshold };
        if (valid) Assert.IsNotNull(policy.Rules());
        else Assert.Throws<FormatException>(() => policy.Rules());
    }

    [TestMethod]
    [DataRow("roundRobin", "/v1/responses", null)]
    [DataRow("random", "/v1/responses", null)]
    [DataRow("priority", "/v1/messages", null)]
    [DataRow("roundRobin", "/v1/chat/completions", null)]
    [DataRow("roundRobin", "/v1/chat/completions", "x-api-key")]
    [DataRow("priority", "/v1/messages", "Authorization")]
    public async Task SwitchRebuildsKeyHeadersAndPreservesBody(string mode, string endpoint, string? authenticationHeader)
    {
        var account = Account(threshold: 1);
        Edit(account, s => { s["keySelectionMode"] = mode; s["requestPolicy"]!["statusCodeMapping"] = new JsonObject { ["403"] = 200 }; });
        if (authenticationHeader is not null)
            Edit(account, s => s["extraParams"] = AuthenticationSelectionTests.Authentication(authenticationHeader).ToJsonString());
        var host = ChannelKeysTests.Host(account);
        var auth = new List<string>(); var bodies = new List<string>();
        var client = Client(async request =>
        {
            var secret = request.Headers.GetValues("X-Selected").Single();
            auth.Add(secret); bodies.Add(await request.Content!.ReadAsStringAsync());
            AuthenticationSelectionTests.AssertAuthentication(request,
                authenticationHeader ?? (endpoint == "/v1/messages" ? "x-api-key" : "Authorization"), secret);
            return Response(auth.Count == 1 ? 403 : 200, endpoint);
        });
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.InvokeAsync(Attempt(account, client, endpoint: endpoint));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(2, auth.Count); Assert.AreNotEqual(auth[0], auth[1]); Assert.AreEqual(bodies[0], bodies[1]);
        var disabled = ReadKeys(account).Single(k => !k.Enabled);
        Assert.AreEqual(auth[0], disabled.Secret); Assert.AreEqual(403, disabled.AutoDisabledStatusCode);
        Assert.AreEqual("forward", disabled.AutoDisabledSource); Assert.IsNotNull(disabled.AutoDisabledAt);
        using var restarted = new UniversalForwardTerminal(host);
        var afterRestart = Client(r => { Assert.AreEqual(auth[1], r.Headers.GetValues("X-Selected").Single()); return Task.FromResult(Response(200)); });
        await restarted.InvokeAsync(Attempt(account, afterRestart, "next"));
    }

    [TestMethod]
    [DataRow(false, 3, 4)]
    [DataRow(true, 0, 1)]
    [DataRow(true, 3, 2)]
    public async Task SwitchHonorsEnableAndExistingRetryBudget(bool enabled, int retries, int sends)
    {
        var account = Account(threshold: 3, retries: retries, enabled: enabled);
        var count = 0;
        var client = Client(_ => { count++; return Task.FromResult(Response(403)); });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        await terminal.InvokeAsync(Attempt(account, client));
        Assert.AreEqual(sends, count); Assert.IsTrue(ReadKeys(account).All(k => k.Enabled));
    }

    [TestMethod]
    public async Task RepeatedHostAttemptDoesNotReuseFailedKeyOrIncrementTwice()
    {
        var account = Account(retries: 0);
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var sent = new List<string>();
        var client = Client(r => { sent.Add(r.Headers.GetValues("X-Selected").Single()); return Task.FromResult(Response(401)); });
        for (var n = 0; n < 5; n++) await terminal.InvokeAsync(Attempt(account, client));
        Assert.AreEqual(2, sent.Count); Assert.AreEqual(2, sent.Distinct().Count());
        for (var n = 0; n < 2; n++)
        {
            await terminal.InvokeAsync(Attempt(account, client, "new-" + n));
            await terminal.InvokeAsync(Attempt(account, client, "new-" + n));
        }
        Assert.IsTrue(ReadKeys(account).All(k => !k.Enabled));
    }

    [TestMethod]
    public void CounterResetsAndOldGenerationCannotBlockNewKey()
    {
        var tracker = new KeyHealthTracker(); var key = Keys()[0]; var policy = new KeyAutoDisablePolicy();
        tracker.Observe("channel", key, "one", true, 401, policy, "forward");
        tracker.Observe("channel", key, "one", true, 401, policy, "forward");
        Assert.AreEqual(1, tracker.For("channel", key).Failures);
        tracker.Observe("channel", key, "two", false, 500, policy, "forward");
        Assert.AreEqual(0, tracker.For("channel", key).Failures);
        foreach (var trace in new[] { "three", "four", "five" }) tracker.Observe("channel", key, trace, true, 403, policy, "forward");
        Assert.IsTrue(tracker.IsBlocked("channel", key));
        Assert.IsFalse(tracker.IsBlocked("channel", key with { HealthVersion = 1 }));
        Assert.IsFalse(tracker.IsBlocked("channel", key with { Secret = "replacement" }));
    }

    [TestMethod]
    public async Task CasFailureExcludesKeyAndNextRequestPersistsWithoutOverwritingSettings()
    {
        var account = Account(threshold: 1);
        var host = ChannelKeysTests.Host(account, cas: false);
        using var terminal = new UniversalForwardTerminal(host);
        var client = Client(r => Task.FromResult(Response(r.Headers.GetValues("X-Selected").Single() == "secret-a" ? 403 : 200)));
        await terminal.InvokeAsync(Attempt(account, client));
        Assert.IsTrue(ReadKeys(account).All(k => k.Enabled));
        var card = JsonSerializer.SerializeToElement((await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body, Json);
        var a = card.GetProperty("accounts")[0].GetProperty("keys")[0];
        Assert.IsFalse(a.GetProperty("enabled").GetBoolean());
        Assert.IsFalse(string.IsNullOrEmpty(a.GetProperty("health").GetProperty("saveError").GetString()));
        Edit(account, s => s["weight"] = 42);
        Mock.Get(host.Services.Accounts).Setup(x => x.CompareExchangeCredentialAsync(account.Id, It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long _, Credential credential, CancellationToken _) => { account.Credential = credential; account.CredentialVersion++; return account; });
        await terminal.InvokeAsync(Attempt(account, client, "next"));
        Assert.IsFalse(ReadKeys(account)[0].Enabled);
        Assert.AreEqual(42, ChannelKeysTests.Settings(account)["weight"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ManualRecoveryRejectsStaleEditorAndLateResponse()
    {
        var account = Account(threshold: 1, retries: 0);
        Edit(account, s => s["keySelectionMode"] = "priority");
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = terminal.InvokeAsync(Attempt(account, Client(async _ => { entered.SetResult(); await release.Task; return Response(403); }), "late"));
        await entered.Task;
        await terminal.InvokeAsync(Attempt(account, Client(_ => Task.FromResult(Response(403))), "disable"));
        var stale = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = "Channel", keyRevision = 7, keys = Keys() }));
        Assert.AreEqual(409, stale.StatusCode);
        var recovered = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = "Channel", keyRevision = ChannelKeysTests.Settings(account)["keyRevision"]!.GetValue<long>(), keys = Keys() }));
        Assert.AreEqual(200, recovered.StatusCode);
        release.SetResult(); await late;
        Assert.IsTrue(ReadKeys(account)[0].Enabled); Assert.IsNull(ReadKeys(account)[0].AutoDisabledAt);
    }

    [TestMethod]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    public async Task TestCheckboxIsIndependentAndStopsPinnedKeyRetries(bool formalEnabled, bool testEnabled, bool staysEnabled)
    {
        var account = Account(threshold: 1, retries: 3, enabled: formalEnabled);
        var host = ChannelKeysTests.Host(account); var sends = 0;
        using var handler = new Handler(_ => { sends++; return Response(403); });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = account.Id, models = RepeatedModels, keyIds = AlphaKey, autoDisableKeys = testEnabled }), _ => { }, CancellationToken.None));
        Assert.AreEqual(staysEnabled, ReadKeys(account)[0].Enabled);
        Assert.AreEqual(testEnabled ? 1 : 8, sends);
        Assert.IsFalse(result!.Value.TryGetProperty("rows", out _));
        var rows = ConnectionTestAssertions.Rows(terminal);
        if (testEnabled) Assert.AreEqual("skipped", rows[1].GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task WebSocketHandshake403DisablesKey()
    {
        await using var server = new WebSocketLoopback((peer, _, ct) => peer.RejectAsync(403, "denied", ct));
        var account = Account(threshold: 1, retries: 0);
        Edit(account, s => { s["baseUrl"] = server.Address.GetLeftPart(UriPartial.Authority); s["responsesTransport"] = "websocket"; });
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => server.CreateClient());
        using var terminal = new UniversalForwardTerminal(host);
        await terminal.InvokeAsync(Attempt(account, Mock.Of<IPluginHttpClient>()));
        Assert.IsFalse(ReadKeys(account)[0].Enabled); await server.Completion;
    }

    [TestMethod]
    public async Task OversizedEscapedTestInputIsRejectedBeforeStartingJob()
    {
        var account = Account(); var host = ChannelKeysTests.Host(account);
        var jobs = new Mock<IPluginJobs>(); Mock.Get(host.Services).SetupGet(s => s.Jobs).Returns(jobs.Object);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.StartConnectionTestsAsync(ChannelKeysTests.Context(new
        { accountId = account.Id, models = RepeatedModels, content = new string('中', 16000) }));
        Assert.AreEqual(400, result.StatusCode);
        StringAssert.Contains(JsonSerializer.Serialize(result.Body, Json), "64 KiB");
        jobs.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<JsonElement?>(), It.IsAny<PluginJobOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task NetworkFailureDoesNotResetCounterAndStaleAccountCannotReselectDisabledKey()
    {
        var account = Account(threshold: 2, retries: 0);
        Edit(account, s => s["keySelectionMode"] = "priority");
        var stale = new Account { Id = account.Id, Platform = account.Platform, PluginKey = account.PluginKey, Credential = account.Credential };
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        await terminal.InvokeAsync(Attempt(account, Client(_ => Task.FromResult(Response(401))), "first"));
        await terminal.InvokeAsync(Attempt(account, Client(_ => throw new HttpRequestException("network failure")), "network"));
        await terminal.InvokeAsync(Attempt(account, Client(_ => Task.FromResult(Response(403))), "second"));
        Assert.IsFalse(ReadKeys(account)[0].Enabled);
        var client = Client(r => { Assert.AreEqual("secret-b", r.Headers.GetValues("X-Selected").Single()); return Task.FromResult(Response(200)); });
        await terminal.InvokeAsync(Attempt(stale, client, "stale-account"));
        Assert.AreEqual(1, Mock.Get(client).Invocations.Count);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
}
