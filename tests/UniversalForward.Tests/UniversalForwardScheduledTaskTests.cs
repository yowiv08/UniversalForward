using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class UniversalForwardScheduledTaskTests
{
    [TestMethod]
    public void UniversalForwardDoesNotRegisterScheduledTasks()
    {
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        Assert.IsFalse(typeof(Router.Contracts.Plugins.IPluginScheduledTaskProvider)
            .IsAssignableFrom(terminal.GetType()));
    }
}
