using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

[TestClass]
public sealed class UniversalForwardManagementTests
{
    private static readonly string[] ManualModels = ["vendor/manual-model"];
    private static readonly string[] DiscoveredModels = ["different-discovered-model"];

    [TestMethod]
    public async Task ManualModelsCanBeSavedWithoutDiscovery()
    {
        var host = PluginTestHost.Create("universalforward");
        Account? saved = null;
        Mock.Get(host.Services.Accounts)
            .Setup(x => x.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account account, CancellationToken _) => { saved = account; return account; });
        Mock.Get(host.Services.Accounts)
            .Setup(x => x.ListAsync("universalforward", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => saved is null ? Array.Empty<Account>() : new[] { saved });
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.SaveAccountAsync(new PluginHttpContext
        {
            PluginKey = "universalforward", Platform = "universalforward",
            Body = JsonSerializer.SerializeToElement(new
            {
                label = "manual", baseUrl = "https://upstream.example/v1", apiKey = "test-only",
                models = ManualModels,
                availableModels = DiscoveredModels
            })
        });
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsNotNull(saved);
        var fields = ((CustomCredential)saved.Credential).Fields;
        CollectionAssert.AreEqual(ManualModels, JsonSerializer.Deserialize<string[]>(fields["models"]!)!);
        Assert.IsFalse(fields["settings"]!.Contains("autoCheckIn", StringComparison.Ordinal));
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    public void RemovedOperationsAreNotExposed()
    {
        var methods = typeof(UniversalForwardTerminal).GetMethods().Select(x => x.Name).ToArray();
        Assert.IsFalse(methods.Contains("RefreshQuotaAsync"));
        Assert.IsFalse(methods.Contains("RunCheckInAsync"));
        Assert.IsFalse(typeof(IPluginScheduledTaskProvider).IsAssignableFrom(typeof(UniversalForwardTerminal)));
    }

    [TestMethod]
    public async Task LegacyAccountCanChangeConnectionWithoutRediscovery()
    {
        var host = PluginTestHost.Create("universalforward");
        var existing = new Account
        {
            Id = "legacy", PluginKey = "universalforward", Platform = "universalforward",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = """{"baseUrl":"https://old.example/v1","apiKey":"keep-key","siteType":"NewAPI","username":"old","password":"unused","autoCheckIn":true}""",
                ["models"] = """["vendor/model"]""", ["modelsConfigured"] = "true",
                ["quotaSnapshot"] = """{"old":true}""", ["unrelated"] = "keep"
            })
        };
        Account? saved = null;
        Mock.Get(host.Services.Accounts).Setup(x => x.GetAsync("legacy", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        Mock.Get(host.Services.Accounts).Setup(x => x.CompareExchangeCredentialAsync("legacy", existing.CredentialVersion, It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long _, Credential credential, CancellationToken _) => { existing.Credential = credential; saved = existing; return existing; });
        Mock.Get(host.Services.Accounts).Setup(x => x.PatchAsync(It.IsAny<Account>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account account, IReadOnlyList<string> _, CancellationToken _) => { existing.Label = account.Label; return existing; });
        Mock.Get(host.Services.Accounts).Setup(x => x.ListAsync("universalforward", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => saved is null ? new[] { existing } : new[] { saved });
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.SaveAccountAsync(new PluginHttpContext
        {
            PluginKey = "universalforward", Platform = "universalforward",
            Body = JsonSerializer.SerializeToElement(new
            {
                id = "legacy", label = "updated", baseUrl = "https://new.example/v1", apiKey = ""
            })
        });
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsNotNull(saved);
        var fields = ((CustomCredential)saved.Credential).Fields;
        Assert.AreEqual("""["vendor/model"]""", fields["models"]);
        Assert.AreEqual("keep", fields["unrelated"]);
        Assert.AreEqual("""{"old":true}""", fields["quotaSnapshot"]);
        using var settings = JsonDocument.Parse(fields["settings"]!);
        Assert.AreEqual("keep-key", settings.RootElement.GetProperty("keys")[0].GetProperty("secret").GetString());
        Assert.IsFalse(settings.RootElement.TryGetProperty("password", out _));
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }
}
