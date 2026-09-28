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
public sealed class MultiKeyOperationsTests
{
    private static readonly string[] Models = ["model"];
    private static readonly string[] FirstGeneratedModel = ["model0"];
    private static readonly string[] KeyIds = ["a", "b"];
    private static readonly string[] RetryHeaders = ["Bearer secret-a", "Bearer secret-a", "Bearer secret-a"];
    private static readonly string[] SelectedSecrets = ["secret-a", "secret-b"];

    private static List<ChannelKey> Keys() =>
        [new() { Id = "a", Name = "Alpha", Secret = "secret-a" }, new() { Id = "b", Name = "Beta", Secret = "secret-b" }];

    [TestMethod]
    [DataRow("all", 2)]
    [DataRow("specified", 1)]
    [DataRow("strategy", 1)]
    public async Task StartStoresOnlyIdsAndValidatesCombinations(string mode, int expected)
    {
        var host = JobHost(out var account);
        JsonElement? input = null;
        Mock.Get(host.Services.Jobs).Setup(x => x.StartAsync("connection-test", It.IsAny<JsonElement?>(),
            It.IsAny<PluginJobOptions>(), It.IsAny<CancellationToken>()))
            .Callback((string _, JsonElement? value, PluginJobOptions _, CancellationToken _) => input = value);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.StartConnectionTestsAsync(ChannelKeysTests.Context(new
        { accountId = account.Id, models = Models, keyMode = mode, keyId = "a" }));
        Assert.AreEqual(202, result.StatusCode);
        Assert.AreEqual(expected, input!.Value.GetProperty("keyIds").GetArrayLength());
        Assert.IsFalse(input.Value.GetRawText().Contains("secret-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MoreThan100CombinationsAndDisabledSpecifiedKeyAreRejected()
    {
        var host = JobHost(out var account);
        var fields = new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["models"] = JsonSerializer.Serialize(Enumerable.Range(0, 51).Select(i => $"model{i}")) };
        account.Credential = new CustomCredential(fields);
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(400, (await terminal.StartConnectionTestsAsync(ChannelKeysTests.Context(new
        { accountId = account.Id, models = Enumerable.Range(0, 51).Select(i => $"model{i}"), keyMode = "all" }))).StatusCode);
        SetKeys(account, [Keys()[0] with { Enabled = false }, Keys()[1]]);
        Assert.AreEqual(400, (await terminal.StartConnectionTestsAsync(ChannelKeysTests.Context(new
        { accountId = account.Id, models = FirstGeneratedModel, keyMode = "specified", keyId = "a" }))).StatusCode);
        Mock.Get(host.Services.Jobs).Verify(x => x.StartAsync(It.IsAny<string>(), It.IsAny<JsonElement?>(),
            It.IsAny<PluginJobOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchKeysAreSerialPinnedAndRevalidated(bool removeQueued)
    {
        var host = JobHost(out var account, retries: 2);
        var auth = new List<string>();
        using var handler = new Handler(request =>
        {
            auth.Add(request.Headers.Authorization!.ToString());
            if (removeQueued) SetKeys(account, [Keys()[0]]);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var progress = new List<JsonElement>();
        var result = await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = account.Id, models = Models, keyIds = KeyIds }),
            p => progress.Add(p!.Value), CancellationToken.None));
        var rows = result!.Value.GetProperty("rows");
        Assert.AreEqual(2, rows.GetArrayLength());
        Assert.AreEqual("a", rows[0].GetProperty("keyId").GetString());
        Assert.AreEqual("b", rows[1].GetProperty("keyId").GetString());
        Assert.AreEqual(2, rows[0].GetProperty("retries").GetInt32());
        Assert.AreEqual(removeQueued ? 3 : 6, auth.Count);
        CollectionAssert.AreEqual(RetryHeaders, auth.Take(3).ToArray());
        if (removeQueued) Assert.IsTrue(rows[1].GetProperty("error").GetString()!.Contains("指定 Key", StringComparison.Ordinal));
        else Assert.IsTrue(auth.Skip(3).All(x => x == "Bearer secret-b"));
        Assert.AreEqual(2, progress[^1].GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task BatchCancellationStopsQueuedCombinations()
    {
        var host = JobHost(out var account);
        using var cts = new CancellationTokenSource();
        var count = 0;
        using var handler = new Handler(_ =>
        {
            count++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Registration(terminal).ExecuteAsync(new PluginJobContext(
            "job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = account.Id, models = Models, keyIds = KeyIds }),
            _ => { }, cts.Token)));
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task DiscoveryAndRefreshUseSpecifiedKeyWithoutAdvancingRoundRobin()
    {
        var account = ChannelKeysTests.Account(Keys());
        var host = ChannelKeysTests.Host(account);
        var auth = new List<string>();
        using var handler = new Handler(request =>
        {
            auth.Add(request.Headers.Authorization!.ToString());
            Assert.AreEqual("secret-b", request.Headers.GetValues("X-Selected").Single());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"model"}]}""") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { id = account.Id, keyId = "b" }))).StatusCode);
        Assert.AreEqual(200, (await terminal.RefreshModelsAsync(ChannelKeysTests.Context(new { id = account.Id, keyId = "b" }))).StatusCode);
        Assert.AreEqual(2, auth.Count);
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) =>
            {
                Assert.AreEqual("Bearer secret-a", request.Headers.Authorization!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            });
        await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, client.Object));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task RefreshConflictCannotOverwriteEditedKeys()
    {
        var account = ChannelKeysTests.Account(Keys());
        var host = ChannelKeysTests.Host(account);
        using var handler = new Handler(_ =>
        {
            SetKeys(account, [Keys()[1]]);
            account.CredentialVersion++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"model"}]}""") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(409, (await terminal.RefreshModelsAsync(ChannelKeysTests.Context(new { id = account.Id }))).StatusCode);
        Assert.AreEqual("b", ChannelKeysTests.Settings(account)["keys"]![0]!["id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FixedAuthenticationOverrideStillWins()
    {
        var account = ChannelKeysTests.Account(Keys());
        var settings = ChannelKeysTests.Settings(account);
        settings["headerOverride"]!["Authorization"] = "Bearer fixed";
        var fields = new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields) { ["settings"] = settings.ToJsonString() };
        account.Credential = new CustomCredential(fields);
        var client = new Mock<IPluginHttpClient>();
        var selected = new List<string>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) =>
            {
                Assert.AreEqual("Bearer fixed", request.Headers.Authorization!.ToString());
                selected.Add(request.Headers.GetValues("X-Selected").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, client.Object));
        await terminal.InvokeAsync(ChannelKeysTests.Attempt(account, client.Object));
        CollectionAssert.AreEqual(SelectedSecrets, selected);
    }

    private static void SetKeys(Account account, List<ChannelKey> keys)
    {
        var settings = ChannelKeysTests.Settings(account);
        settings["keys"] = JsonSerializer.SerializeToNode(keys, JsonSerializerOptions.Web);
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() });
    }

    private static IPluginHost JobHost(out Account account, int retries = 0)
    {
        account = ChannelKeysTests.Account(Keys(), retries);
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services).SetupGet(x => x.Jobs).Returns(Mock.Of<IPluginJobs>());
        return host;
    }

    private static PluginJobRegistration Registration(UniversalForwardTerminal terminal)
    {
        PluginJobRegistration? registration = null;
        var builder = new Mock<IPluginBuilder>();
        builder.Setup(x => x.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(r => registration = r);
        terminal.Configure(builder.Object);
        return registration!;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
