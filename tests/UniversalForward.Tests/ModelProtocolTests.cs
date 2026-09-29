using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ModelProtocolTests
{
    [TestMethod]
    public void MatchingProtocolWinsOverPreference()
    {
        var options = new ModelProtocolOptions { Protocols = ["responses", "messages"], PreferredProtocol = "messages" };
        Assert.AreEqual("/v1/responses", options.Select("/v1/responses"));
        Assert.AreEqual("/v1/messages", options.Select("/v1/messages"));
    }

    [TestMethod]
    public void NativeOnlyRejectsConversion()
    {
        var options = new ModelProtocolOptions { Protocols = ["messages"], PreferredProtocol = "messages", NativeOnly = true };
        Assert.Throws<FormatException>(() => options.Select("/v1/chat/completions"));
        Assert.AreEqual("/v1/messages", options.Select("/v1/messages"));
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("")]
    [DataRow("chat")]
    [DataRow("completions")]
    public void UnknownProtocolsAreRejected(string name)
        => Assert.Throws<FormatException>(() => new ModelProtocolOptions { Protocols = [name], PreferredProtocol = name }.Validate());

    [TestMethod]
    public void PreferenceMustBeSupported()
        => Assert.Throws<FormatException>(() => new ModelProtocolOptions { PreferredProtocol = "messages" }.Validate());

    [TestMethod]
    public void LegacyCompletionsCannotConvert()
    {
        Assert.Throws<FormatException>(() => new ModelProtocolOptions().Select("/v1/completions"));
        var options = new ModelProtocolOptions { Protocols = ["completions"], PreferredProtocol = "completions" };
        Assert.Throws<FormatException>(() => options.Select("/v1/messages"));
        Assert.Throws<FormatException>(() => options.Select("/v1/completions"));
    }
}
