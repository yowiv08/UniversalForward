using System.Text.Json;
using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ResponsesResourceStateSettingsTests
{
    [TestMethod]
    public async Task LegacyDefaultsOffAndSavedSettingSurvivesOmissionAndNull()
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }]);
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var legacy = await CardAsync(terminal);
        Assert.IsFalse(legacy["stripResponsesResourceState"]!.GetValue<bool>());

        var enabled = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, stripResponsesResourceState = true }));
        Assert.AreEqual(200, enabled.StatusCode);
        Assert.IsTrue(ChannelKeysTests.Settings(account)["stripResponsesResourceState"]!.GetValue<bool>());
        Assert.IsTrue(JsonSerializer.SerializeToNode(enabled.Body, RequestLogStore.Json)!["account"]!["stripResponsesResourceState"]!.GetValue<bool>());

        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, weight = 50 }))).StatusCode);
        Assert.IsTrue(ChannelKeysTests.Settings(account)["stripResponsesResourceState"]!.GetValue<bool>());
        Assert.AreEqual(200, (await terminal.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, stripResponsesResourceState = (bool?)null }))).StatusCode);
        Assert.IsTrue(ChannelKeysTests.Settings(account)["stripResponsesResourceState"]!.GetValue<bool>());

        using var reloaded = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        Assert.IsTrue((await CardAsync(reloaded))["stripResponsesResourceState"]!.GetValue<bool>());
        Assert.AreEqual(200, (await reloaded.SaveAccountAsync(ChannelKeysTests.Context(new
        { id = account.Id, label = account.Label, stripResponsesResourceState = false }))).StatusCode);
        Assert.IsFalse(ChannelKeysTests.Settings(account)["stripResponsesResourceState"]!.GetValue<bool>());
        Assert.IsFalse((await CardAsync(reloaded))["stripResponsesResourceState"]!.GetValue<bool>());
    }

    private static async Task<JsonNode> CardAsync(UniversalForwardTerminal terminal)
        => JsonSerializer.SerializeToNode((await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }))).Body,
            RequestLogStore.Json)!["accounts"]![0]!;
}
