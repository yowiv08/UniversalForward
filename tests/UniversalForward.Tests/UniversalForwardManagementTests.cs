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
    public async Task CopiedChannelCreatesIndependentIdsAndCredentials()
    {
        List<ChannelKey> keys = [new() { Id = "primary", Name = "Primary", Secret = "sk-primary" },
            new() { Id = "backup", Name = "Backup", Secret = "sk-backup", Enabled = false }];
        var source = ChannelKeysTests.Account(keys);
        source.Status = new ResourceStatus { State = ResourceState.Disabled };
        var originalCredential = source.Credential;
        var host = ChannelKeysTests.Host(source);
        Account? saved = null;
        Mock.Get(host.Services.Accounts).Setup(x => x.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account account, CancellationToken _) => { saved = account; return account; });
        Mock.Get(host.Services.Accounts).Setup(x => x.ListAsync("universalforward", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => saved is null ? new[] { source } : new[] { source, saved });
        using var terminal = new UniversalForwardTerminal(host);

        var result = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        {
            id = (string?)null, label = "Channel - 副本", baseUrl = "https://upstream.example",
            enabled = false, keyRevision = 0, deletedKeyIds = Array.Empty<string>(), keySelectionMode = "priority",
            keys = keys.Select(key => key with { Id = "" }).ToArray(), models = ManualModels
        }));

        Assert.AreEqual(200, result.StatusCode);
        Assert.IsNotNull(saved);
        Assert.AreNotEqual(source.Id, saved.Id);
        Assert.IsTrue(Guid.TryParse(saved.Id, out _));
        Assert.AreSame(originalCredential, source.Credential);
        Assert.AreNotSame(source.Status, saved.Status);
        Assert.AreEqual(new ResourceStatus().State, saved.Status.State);
        var settings = ChannelKeysTests.Settings(saved);
        Assert.IsFalse(settings["enabled"]!.GetValue<bool>());
        Assert.AreEqual(1, settings["keyRevision"]!.GetValue<int>());
        var savedKeys = settings["keys"]!.Deserialize<List<ChannelKey>>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(keys.Count, savedKeys.Count);
        Assert.AreEqual(keys.Count, savedKeys.Select(key => key.Id).Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < keys.Count; index++)
        {
            Assert.IsTrue(Guid.TryParse(savedKeys[index].Id, out _));
            Assert.IsFalse(keys.Any(key => key.Id == savedKeys[index].Id));
            Assert.AreEqual(keys[index].Name, savedKeys[index].Name);
            Assert.AreEqual(keys[index].Secret, savedKeys[index].Secret);
            Assert.AreEqual(keys[index].Enabled, savedKeys[index].Enabled);
        }
        var fields = ((CustomCredential)saved.Credential).Fields;
        CollectionAssert.AreEqual(ManualModels, JsonSerializer.Deserialize<string[]>(fields["models"]!)!);
        Assert.IsFalse(fields.ContainsKey("availableModels"));
        Assert.IsFalse(fields.ContainsKey("modelsUpdatedAt"));
        Mock.Get(host.Services.Accounts).Verify(x => x.CompareExchangeCredentialAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()), Times.Never);
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
