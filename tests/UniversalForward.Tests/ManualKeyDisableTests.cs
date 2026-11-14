using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ManualKeyDisableTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] AlphaKey = ["a"];
    private static Account Account() => ChannelKeysTests.Account([
        new ChannelKey { Id = "a", Name = "Alpha", Secret = "secret-a" },
        new ChannelKey { Id = "b", Name = "Beta", Secret = "secret-b" }
    ]);
    private static PluginHttpContext Input(long revision = 7) => ChannelKeysTests.Context(new
        { id = "channel", keyIds = AlphaKey, keyRevision = revision });
    private static ChannelKey[] Keys(Account account) => ChannelKeysTests.Settings(account)["keys"]!.Deserialize<ChannelKey[]>(Json)!;

    [TestMethod]
    public async Task DisablesOnlySelectedKeysPreservesConfigurationAndInvalidatesStaleEditors()
    {
        var account = Account();
        KeyAutoDisableTests.Edit(account, settings => settings["customExtension"] = new JsonObject { ["keep"] = true });
        var host = ChannelKeysTests.Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DisableAccountKeysAsync(Input());
        Assert.AreEqual(200, result.StatusCode);
        var keys = Keys(account);
        Assert.IsFalse(keys[0].Enabled);
        Assert.AreEqual(1L, keys[0].HealthVersion);
        Assert.AreEqual("secret-a", keys[0].Secret);
        Assert.IsNull(keys[0].AutoDisabledAt);
        Assert.IsNull(keys[0].AutoDisabledSource);
        Assert.IsTrue(keys[1].Enabled);
        Assert.AreEqual(0L, keys[1].HealthVersion);
        Assert.AreEqual(8L, ChannelKeysTests.Settings(account)["keyRevision"]!.GetValue<long>());
        Assert.IsTrue(ChannelKeysTests.Settings(account)["customExtension"]!["keep"]!.GetValue<bool>());
        Assert.AreEqual(409, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
            { id = account.Id, label = account.Label, keyRevision = 7, keys }))).StatusCode);

        using var restarted = new UniversalForwardTerminal(host);
        var cards = JsonSerializer.SerializeToElement((await restarted.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body, Json);
        var card = cards.GetProperty("accounts")[0];
        Assert.AreEqual(1, card.GetProperty("enabledKeyCount").GetInt32());
        Assert.AreEqual(1L, card.GetProperty("keys")[0].GetProperty("healthVersion").GetInt64());
    }

    [TestMethod]
    public async Task RepeatedDisableDoesNotIncrementRevisionOrHealthVersion()
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        await terminal.DisableAccountKeysAsync(Input());
        var result = await terminal.DisableAccountKeysAsync(Input(8));
        Assert.AreEqual(200, result.StatusCode);
        var payload = JsonSerializer.SerializeToElement(result.Body, Json);
        Assert.AreEqual(0, payload.GetProperty("disabledCount").GetInt32());
        Assert.AreEqual(8L, ChannelKeysTests.Settings(account)["keyRevision"]!.GetValue<long>());
        Assert.AreEqual(1L, Keys(account)[0].HealthVersion);
        Mock.Get(host.Services.Accounts).Verify(x => x.CompareExchangeCredentialAsync(
            account.Id, It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("null")]
    [DataRow("blank")]
    [DataRow("tooMany")]
    [DataRow("unknown")]
    [DataRow("noRevision")]
    [DataRow("negativeRevision")]
    public async Task RejectsInvalidSelectionWithoutWriting(string scenario)
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        string[]? keyIds = scenario switch
        {
            "empty" => [], "null" => null, "blank" => [" "], "unknown" => ["foreign"],
            "tooMany" => Enumerable.Repeat("a", 101).ToArray(), _ => AlphaKey
        };
        long? revision = scenario == "noRevision" ? null : scenario == "negativeRevision" ? -1 : 7;
        var result = await terminal.DisableAccountKeysAsync(ChannelKeysTests.Context(new { id = account.Id, keyIds, keyRevision = revision }));
        Assert.AreEqual(400, result.StatusCode);
        Assert.IsTrue(Keys(account).All(k => k.Enabled));
        Mock.Get(host.Services.Accounts).Verify(x => x.CompareExchangeCredentialAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RejectsMissingChannelAndStaleSelection()
    {
        var account = Account();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        Assert.AreEqual(404, (await terminal.DisableAccountKeysAsync(ChannelKeysTests.Context(new
            { id = "missing", keyIds = AlphaKey, keyRevision = 7 }))).StatusCode);
        Assert.AreEqual(409, (await terminal.DisableAccountKeysAsync(Input(6))).StatusCode);
        Assert.IsTrue(Keys(account)[0].Enabled);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentEditPreservesUnrelatedFieldsAndRejectsChangedKeys(bool replaceKey)
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        var calls = 0;
        Mock.Get(host.Services.Accounts).Setup(x => x.CompareExchangeCredentialAsync(account.Id,
            It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long version, Credential credential, CancellationToken _) =>
            {
                if (++calls == 1)
                {
                    KeyAutoDisableTests.Edit(account, settings =>
                    {
                        settings["weight"] = 42;
                        if (replaceKey)
                        {
                            settings["keys"]![0]!["secret"] = "replacement-secret";
                            settings["keys"]![0]!["healthVersion"] = 1;
                            settings["keyRevision"] = 8;
                        }
                    });
                    account.CredentialVersion++;
                    return null!;
                }
                if (version != account.CredentialVersion) return null!;
                account.Credential = credential;
                account.CredentialVersion++;
                return account;
            });
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DisableAccountKeysAsync(Input());
        Assert.AreEqual(replaceKey ? 409 : 200, result.StatusCode);
        Assert.AreEqual(42, ChannelKeysTests.Settings(account)["weight"]!.GetValue<int>());
        Assert.AreEqual(replaceKey, Keys(account)[0].Enabled);
        Assert.AreEqual(replaceKey ? "replacement-secret" : "secret-a", Keys(account)[0].Secret);
        Assert.IsTrue(Keys(account)[1].Enabled);
    }

    [TestMethod]
    public async Task PersistentSaveConflictsReturnConflictWithoutPartialChanges()
    {
        var account = Account();
        var host = ChannelKeysTests.Host(account, cas: false);
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(409, (await terminal.DisableAccountKeysAsync(Input())).StatusCode);
        Assert.IsTrue(Keys(account).All(k => k.Enabled));
        Mock.Get(host.Services.Accounts).Verify(x => x.CompareExchangeCredentialAsync(
            account.Id, It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
