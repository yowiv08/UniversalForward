using System.Collections.Concurrent;
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
public sealed class ChannelKeysTests
{
    private static readonly string[] ReorderedKeyIds = ["b", "a"];
    private static readonly string[] DisabledKeyIds = ["off"];
    private static readonly int[] ConcurrentSaveStatuses = [200, 409];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static List<ChannelKey> Keys() =>
        [new() { Id = "a", Name = "Alpha", Secret = "secret-a" },
         new() { Id = "off", Secret = "secret-off", Enabled = false },
         new() { Id = "b", Name = "Beta", Secret = "secret-b" }];

    [TestMethod]
    public void LegacyAndExplicitEmptyAreDistinct()
    {
        Assert.AreEqual("legacy", ChannelKeys.Read(null, "old")[0].Id);
        Assert.AreEqual(0, ChannelKeys.Read([], "old").Count);
    }

    [TestMethod]
    public void MergePreservesMissingAndBlankAndDeletesOnlyExplicitly()
    {
        var keys = Keys();
        var result = ChannelKeys.Merge(keys, [keys[2] with { Secret = "", Name = "Renamed" }], ["off"]);
        CollectionAssert.AreEqual(ReorderedKeyIds, result.Select(k => k.Id).ToArray());
        Assert.AreEqual("secret-b", result[0].Secret);
        Assert.AreEqual("Renamed", result[0].Name);
        Assert.AreEqual("Beta", keys[2].Name);
    }

    [TestMethod]
    [DataRow("foreign")]
    [DataRow("empty")]
    [DataRow("duplicate")]
    [DataRow("deleteForeign")]
    [DataRow("deleteAndEdit")]
    public void InvalidEditsAreRejected(string scenario)
    {
        Assert.Throws<FormatException>(() => ChannelKeys.Merge(Keys(), scenario switch
        {
            "foreign" => [new ChannelKey { Id = "foreign", Secret = "x" }],
            "empty" => [new ChannelKey()],
            "duplicate" => [Keys()[0], Keys()[0]],
            "deleteAndEdit" => [Keys()[0]],
            _ => []
        }, scenario == "deleteForeign" ? ["foreign"] : scenario == "deleteAndEdit" ? ["a"] : []));
    }

    [TestMethod]
    public void LimitsAndCaseSensitiveSecrets()
    {
        var keys = Enumerable.Range(0, 100).Select(i => new ChannelKey { Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Secret = $"key{i}" }).ToList();
        ChannelKeys.Validate(keys, "roundRobin");
        keys.Add(new() { Id = "101", Secret = "unique" });
        Assert.Throws<FormatException>(() => ChannelKeys.Validate(keys, "random"));
        ChannelKeys.Validate([new() { Id = "1", Secret = "ABC" }, new() { Id = "2", Secret = "abc" }], "priority");
        Assert.Throws<FormatException>(() => ChannelKeys.Validate([Keys()[0], Keys()[2] with { Secret = "secret-a" }], "priority"));
        Assert.Throws<FormatException>(() => ChannelKeys.Validate(Keys(), "invalid"));
    }

    [TestMethod]
    public void RoundRobinDiscoveryPriorityAndReset()
    {
        var selector = new ChannelKeySelector();
        var keys = Keys();
        Assert.AreEqual("a", selector.Select("one", keys, "roundRobin").Id);
        Assert.AreEqual("a", selector.Select("two", keys, "roundRobin").Id);
        Assert.AreEqual("a", selector.Select("one", keys, "roundRobin", discovery: true).Id);
        Assert.AreEqual("b", selector.Select("one", keys, "roundRobin").Id);
        Assert.AreEqual("a", selector.Select("one", keys, "priority").Id);
        keys.Reverse();
        Assert.AreEqual("b", selector.Select("one", keys, "roundRobin").Id);
        selector.Remove("one");
        Assert.AreEqual("b", selector.Select("one", keys, "roundRobin").Id);
        selector.Clear();
        Assert.AreEqual("b", selector.Select("one", keys, "roundRobin").Id);
    }

    [TestMethod]
    public void DisabledAndSpecifiedKeysNeverFallback()
    {
        var selector = new ChannelKeySelector();
        Assert.Throws<FormatException>(() => selector.Select("one", Keys(), "random", "off"));
        Assert.Throws<FormatException>(() => selector.Select("one", Keys(), "random", "unknown"));
        Assert.Throws<FormatException>(() => selector.Select("one", [], "priority"));
        for (var i = 0; i < 500; i++) Assert.IsTrue(selector.Select("one", Keys(), "random").Id is "a" or "b");
    }

    [TestMethod]
    public void ConcurrentRoundRobinIsBalancedAndChannelLocal()
    {
        var selector = new ChannelKeySelector();
        var counts = new ConcurrentDictionary<string, int>();
        Parallel.For(0, 10000, _ =>
        {
            var id = selector.Select("one", Keys(), "roundRobin").Id;
            counts.AddOrUpdate(id, 1, (_, count) => count + 1);
        });
        Assert.AreEqual(5000, counts["a"]);
        Assert.AreEqual(5000, counts["b"]);
        Assert.AreEqual("a", selector.Select("two", Keys(), "roundRobin").Id);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    public async Task RetriesPinKeyAndPayloadDespiteCredentialEdit(int retries)
    {
        var account = Account(Keys(), retries);
        var client = new Mock<IPluginHttpClient>();
        var auth = new List<string>();
        var bodies = new List<string>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                auth.Add(request.Headers.Authorization!.ToString());
                Assert.AreEqual("secret-a", request.Headers.GetValues("X-Selected").Single());
                bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                account.Credential = Account([Keys()[2]]).Credential;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("retry") };
            });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        await terminal.InvokeAsync(Attempt(account, client.Object));
        Assert.AreEqual(retries + 1, auth.Count);
        Assert.IsTrue(auth.All(x => x == "Bearer secret-a"));
        Assert.AreEqual(1, bodies.Distinct().Count());
        var next = new Mock<IPluginHttpClient>();
        next.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) =>
            {
                Assert.AreEqual("Bearer secret-b", request.Headers.Authorization!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            });
        await terminal.InvokeAsync(Attempt(account, next.Object));
        Assert.AreEqual(1, next.Invocations.Count);
    }

    [TestMethod]
    public async Task EmptyKeysDoNotSendOrAdvertiseModels()
    {
        var account = Account([]);
        var host = Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        var client = Mock.Of<IPluginHttpClient>();
        Assert.AreEqual(400, (await terminal.InvokeAsync(Attempt(account, client))).Response.StatusCode);
        Assert.AreEqual(0, Mock.Get(client).Invocations.Count);
        var models = await terminal.GetModelsAsync(new ModelQueryContext
        { ForceRefresh = true }, CancellationToken.None);
        Assert.AreEqual(0, models.Count);
    }

    [TestMethod]
    public async Task SaveUsesCasPreservesStatusAndDoesNotExposeSecrets()
    {
        var account = Account(Keys());
        account.Status = new ResourceStatus { State = ResourceState.Disabled };
        var host = Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.SaveAccountAsync(Context(new
        {
            id = account.Id, label = "Channel", keyRevision = 7,
            keys = new[] { new { id = "b", name = "Renamed", secret = "", enabled = true } },
            deletedKeyIds = DisabledKeyIds
        }));
        Assert.AreEqual(200, result.StatusCode);
        var settings = Settings(account);
        Assert.AreEqual(8, settings["keyRevision"]!.GetValue<int>());
        Assert.AreEqual("secret-b", settings["keys"]![0]!["secret"]!.GetValue<string>());
        Assert.AreEqual(2, settings["keys"]!.AsArray().Count);
        Assert.AreEqual(ResourceState.Disabled, account.Status.State);
        Mock.Get(host.Services.Accounts).Verify(x => x.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()), Times.Never);
        var card = JsonSerializer.Serialize(result, Json);
        Assert.IsFalse(card.Contains("secret-a", StringComparison.Ordinal));
        Assert.IsFalse(card.Contains("secret-b", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("revision", 409)]
    [DataRow("foreign", 400)]
    [DataRow("duplicate", 400)]
    [DataRow("ambiguous", 400)]
    [DataRow("cas", 409)]
    public async Task SaveRejectsInvalidAndConcurrentChanges(string scenario, int status)
    {
        var account = Account(Keys());
        var host = Host(account, cas: scenario != "cas");
        using var terminal = new UniversalForwardTerminal(host);
        var input = new JsonObject
        {
            ["id"] = account.Id, ["label"] = "Channel", ["keyRevision"] = scenario == "revision" ? 6 : 7,
            ["keys"] = JsonSerializer.SerializeToNode(new[] { new
            {
                id = scenario == "foreign" ? "other-channel-key" : "a", name = "Alpha", enabled = true,
                secret = scenario == "duplicate" ? "secret-b" : ""
            } })
        };
        if (scenario == "ambiguous") input["APIKEY"] = "ambiguous";
        var before = ((CustomCredential)account.Credential).Fields["settings"];
        Assert.AreEqual(status, (await terminal.SaveAccountAsync(Context(input))).StatusCode);
        Assert.AreEqual(before, ((CustomCredential)account.Credential).Fields["settings"]);
    }

    [TestMethod]
    public async Task NoKeyEditKeepsRevisionAndMissingList()
    {
        var account = Account(Keys());
        using var terminal = new UniversalForwardTerminal(Host(account));
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(Context(new { id = account.Id, label = "Channel", weight = 9 }))).StatusCode);
        var settings = Settings(account);
        Assert.AreEqual(7, settings["keyRevision"]!.GetValue<int>());
        Assert.AreEqual(3, settings["keys"]!.AsArray().Count);
    }

    [TestMethod]
    [DataRow("""[null]""")]
    [DataRow("""[""]""")]
    [DataRow("""[" "]""")]
    [DataRow("""["foreign"]""")]
    public async Task InvalidDeletionIdsReturnBadRequestWithoutWriting(string deletedJson)
    {
        var account = Account(Keys());
        var host = Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        var before = ((CustomCredential)account.Credential).Fields["settings"];
        var input = new JsonObject
        {
            ["id"] = account.Id, ["label"] = "Channel", ["keyRevision"] = 7,
            ["deletedKeyIds"] = JsonNode.Parse(deletedJson)
        };
        Assert.AreEqual(400, (await terminal.SaveAccountAsync(Context(input))).StatusCode);
        Assert.AreEqual(before, ((CustomCredential)account.Credential).Fields["settings"]);
        Mock.Get(host.Services.Accounts).Verify(x => x.CompareExchangeCredentialAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ConcurrentSavesOnlyOneCredentialCasSucceeds()
    {
        var account = Account(Keys());
        var host = Host(account);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var gate = new object();
        Mock.Get(host.Services.Accounts).Setup(x => x.CompareExchangeCredentialAsync(account.Id,
            It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, long version, Credential credential, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref calls) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(ct);
                lock (gate)
                {
                    if (version != account.CredentialVersion) return null!;
                    account.Credential = credential;
                    account.CredentialVersion++;
                    return account;
                }
            });
        using var terminal = new UniversalForwardTerminal(host);
        var first = terminal.SaveAccountAsync(Context(new
        { id = account.Id, label = "Channel", keyRevision = 7, keySelectionMode = "priority" }));
        var second = terminal.SaveAccountAsync(Context(new
        { id = account.Id, label = "Channel", keyRevision = 7, keySelectionMode = "random" }));
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEquivalent(ConcurrentSaveStatuses, results.Select(x => x.StatusCode).ToArray());
        Assert.AreEqual(8, Settings(account)["keyRevision"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task DisabledKeysAreValidCredentialsButNotRoutes()
    {
        var account = Account(Keys().Select(k => k with { Enabled = false }).ToList());
        using var terminal = new UniversalForwardTerminal(Host(account));
        Assert.IsTrue((await terminal.ValidateCredentialAsync(account.Credential, CancellationToken.None)).Success);
        var client = Mock.Of<IPluginHttpClient>();
        Assert.AreEqual(400, (await terminal.InvokeAsync(Attempt(account, client))).Response.StatusCode);
        Assert.AreEqual(0, Mock.Get(client).Invocations.Count);
    }

    internal static Account Account(List<ChannelKey> keys, int retries = 0) => new()
    {
        Id = "channel", Label = "Channel", PluginKey = "universalforward", Platform = "universalforward",
        CredentialVersion = 3,
        Credential = new CustomCredential(new Dictionary<string, string?>
        {
            ["settings"] = JsonSerializer.Serialize(new
            {
                baseUrl = "https://upstream.example", keys, keySelectionMode = "roundRobin", keyRevision = 7,
                requestPolicy = new ForwardRequestPolicy { MaxRetries = retries },
                headerOverride = new Dictionary<string, string> { ["X-Selected"] = "{api_key}" }
            }, Json),
            ["modelsConfigured"] = "true", ["models"] = """["model"]"""
        })
    };

    internal static JsonObject Settings(Account account)
        => JsonNode.Parse(((CustomCredential)account.Credential).Fields["settings"]!)!.AsObject();

    internal static IPluginHost Host(Account account, bool cas = true)
    {
        var host = PluginTestHost.Create("universalforward");
        Mock.Get(host.Services.Accounts).Setup(x => x.GetAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => account);
        Mock.Get(host.Services.Accounts).Setup(x => x.ListAsync("universalforward", It.IsAny<CancellationToken>())).ReturnsAsync(() => new[] { account });
        Mock.Get(host.Services.Accounts).Setup(x => x.CompareExchangeCredentialAsync(account.Id, It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long version, Credential credential, CancellationToken _) =>
            {
                if (!cas || version != account.CredentialVersion) return null!;
                account.Credential = credential;
                account.CredentialVersion++;
                return account;
            });
        return host;
    }

    internal static PluginHttpContext Context(object input) => new()
    {
        PluginKey = "universalforward", Platform = "universalforward", Body = JsonSerializer.SerializeToElement(input, Json)
    };

    internal static PluginAttemptContext Attempt(Account account, IPluginHttpClient client) => new()
    {
        PluginKey = "universalforward", PlatformName = "universalforward", Account = account, HttpClient = client, CancellationToken = CancellationToken.None,
        Request = new AdapterRequest
        {
            Model = "model", Endpoint = "/v1/responses",
            OriginalBody = JsonSerializer.SerializeToElement(new { model = "model", messages = new[] { new { role = "user", content = "OK" } } })
        }
    };
}
