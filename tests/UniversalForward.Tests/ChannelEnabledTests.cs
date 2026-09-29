using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ChannelEnabledTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangesOnlyEnabledAndRefreshesRouting(bool enabled)
    {
        var host = PluginTestHost.Create("universalforward");
        var account = Account();
        var before = new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields);
        Mock.Get(host.Services.Accounts).Setup(x => x.GetAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        Mock.Get(host.Services.Accounts).Setup(x => x.ListAsync("universalforward", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { account });
        Mock.Get(host.Services.Accounts).Setup(x => x.CompareExchangeCredentialAsync(account.Id, 7,
            It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long _, Credential credential, CancellationToken _) => { account.Credential = credential; return account; });
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.SetAccountEnabledAsync(Context(new { id = account.Id, enabled }));
        Assert.AreEqual(200, result.StatusCode);
        var after = ((CustomCredential)account.Credential).Fields;
        foreach (var (name, value) in before.Where(x => x.Key != "settings")) Assert.AreEqual(value, after[name], name);
        var original = JsonNode.Parse(before["settings"]!)!.AsObject();
        var updated = JsonNode.Parse(after["settings"]!)!.AsObject();
        Assert.AreEqual(enabled, updated["enabled"]!.GetValue<bool>());
        original.Remove("enabled"); updated.Remove("enabled");
        Assert.IsTrue(JsonNode.DeepEquals(original, updated));
        Assert.AreEqual("Unchanged label", account.Label);
        Mock.Get(host.Services.Models).Verify(x => x.Invalidate("universalforward"), Times.Once);
        Mock.Get(host.Services.Accounts).Verify(x => x.PatchAsync(It.IsAny<Account>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"id\":\"channel\"}")]
    [DataRow("{\"id\":\"\",\"enabled\":false}")]
    public async Task RequiresExplicitIdAndState(string json)
    {
        var host = PluginTestHost.Create("universalforward");
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.SetAccountEnabledAsync(Context(JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.AreEqual(400, result.StatusCode);
        Mock.Get(host.Services.Accounts).Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task MissingOrConcurrentAccountIsNotOverwritten()
    {
        var host = PluginTestHost.Create("universalforward");
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(404, (await terminal.SetAccountEnabledAsync(Context(new { id = "channel", enabled = false }))).StatusCode);
        var account = Account();
        Mock.Get(host.Services.Accounts).Setup(x => x.GetAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        Assert.AreEqual(409, (await terminal.SetAccountEnabledAsync(Context(new { id = account.Id, enabled = false }))).StatusCode);
        Mock.Get(host.Services.Models).Verify(x => x.Invalidate(It.IsAny<string>()), Times.Never);
    }

    private static Account Account() => new()
    {
        Id = "channel", Label = "Unchanged label", CredentialVersion = 7, PluginKey = "universalforward", Platform = "universalforward",
        Credential = new CustomCredential(new Dictionary<string, string?>
        {
            ["settings"] = """{"baseUrl":"https://upstream.example","apiKey":"keep-key","enabled":true,"weight":79,"keyRevision":3,"future":{"keep":true}}""",
            ["models"] = """["model"]""", ["modelsConfigured"] = "true", ["availableModels"] = """["other"]""", ["custom"] = "keep"
        })
    };
    private static PluginHttpContext Context(object body) => new()
    {
        PluginKey = "universalforward", Platform = "universalforward", Body = JsonSerializer.SerializeToElement(body)
    };
}
