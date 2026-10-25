using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

[TestClass]
public sealed class PluginSettingsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string _directory = null!;
    [TestInitialize] public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), "universalforward-settings-tests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private UniversalForwardTerminal Terminal(IPluginHost? host = null) => new(host ?? PluginTestHost.Create("universalforward")) { SettingsDirectory = _directory };
    private static PluginHttpContext Context(object? value = null) => new() { Body = JsonSerializer.SerializeToElement(value) };
    private static JsonElement Body(PluginResult result) => JsonSerializer.SerializeToElement(result.Body, Json);

    [TestMethod]
    public async Task DefaultsAreReadOnlyAndAllEndpointsRequireAdmin()
    {
        using var terminal = Terminal();
        Assert.AreEqual(1, Body(await terminal.GetExecutionSettingsAsync(Context())).GetProperty("defaultHostAttempts").GetInt32());
        Assert.AreEqual("host", Body(await terminal.GetAppearanceSettingsAsync(Context())).GetProperty("mode").GetString());
        Assert.IsFalse(Directory.Exists(_directory));
        foreach (var name in new[] { nameof(terminal.GetExecutionSettingsAsync), nameof(terminal.SaveExecutionSettingsAsync),
            nameof(terminal.GetAppearanceSettingsAsync), nameof(terminal.SaveAppearanceSettingsAsync) })
            Assert.AreEqual(PluginAuthPolicy.AdminSession, ((PluginEndpointAttribute)Attribute.GetCustomAttribute(
                typeof(UniversalForwardTerminal).GetMethod(name)!, typeof(PluginEndpointAttribute))!).Auth);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(35)]
    public async Task SavesBeforeStartupPolicyAndOnlyUpdatesOwnAttemptLimit(int attempts)
    {
        var host = PluginTestHost.Create("universalforward");
        var initial = new PluginPolicySnapshot(new PluginProxyPolicyBuilder().MaxAttempts(1).AttemptTimeoutSeconds(211).Build(),
            new PluginAccountPolicyBuilder().WeightBy((_, _) => 77).Build());
        var package = initial;
        var platform = initial with { Attempts = initial.Attempts with { TotalTimeout = TimeSpan.FromSeconds(95) } };
        var previous = platform;
        var registry = new Mock<IPluginPolicyRegistry>(MockBehavior.Strict);
        registry.Setup(value => value.GetSnapshot("universalforward")).Returns(() => package);
        registry.Setup(value => value.GetSnapshot("universalforward", "universalforward")).Returns(() => platform);
        registry.Setup(value => value.Set("universalforward", It.IsAny<PluginProxyPolicy>(), It.IsAny<PluginAccountPolicy>()))
            .Callback((string _, PluginProxyPolicy attempts, PluginAccountPolicy accounts) => package = new(attempts, accounts));
        registry.Setup(value => value.Set("universalforward", "universalforward", It.IsAny<PluginProxyPolicy>(), It.IsAny<PluginAccountPolicy>()))
            .Callback((string _, string _, PluginProxyPolicy attempts, PluginAccountPolicy accounts) => platform = new(attempts, accounts));
        Mock.Get(host).Setup(value => value.GetServiceAsync<IPluginPolicyRegistry>()).ReturnsAsync(registry.Object);
        using (var terminal = Terminal(host))
        {
            Assert.AreEqual(200, (await terminal.SaveExecutionSettingsAsync(Context(new { defaultHostAttempts = attempts }))).StatusCode);
            Assert.AreEqual(attempts, package.Attempts.MaxAttempts);
            Assert.AreEqual(attempts, platform.Attempts.MaxAttempts);
            Assert.AreEqual(previous.Attempts with { MaxAttempts = attempts }, platform.Attempts);
            Assert.AreSame(previous.Accounts, platform.Accounts);
            Assert.AreEqual(1, previous.Attempts.MaxAttempts, "Captured policies remain immutable.");
        }
        using var restarted = Terminal(host);
        var builder = new Mock<IPluginBuilder>();
        PluginProxyPolicy? startup = null;
        builder.Setup(value => value.ProxyPolicy(It.IsAny<Action<PluginProxyPolicyBuilder>>()))
            .Callback((Action<PluginProxyPolicyBuilder> configure) => { var policy = new PluginProxyPolicyBuilder(); configure(policy); startup = policy.Build(); });
        restarted.Configure(builder.Object);
        Assert.AreEqual(attempts, startup!.MaxAttempts);
        Assert.AreEqual(attempts, Body(await restarted.GetExecutionSettingsAsync(Context())).GetProperty("defaultHostAttempts").GetInt32());
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"defaultHostAttempts\":0}")]
    [DataRow("{\"defaultHostAttempts\":36}")]
    [DataRow("{\"defaultHostAttempts\":1.5}")]
    [DataRow("{\"defaultHostAttempts\":\"3\"}")]
    public async Task InvalidAttemptsNeverWrite(string json)
    {
        using var terminal = Terminal();
        Assert.AreEqual(400, (await terminal.SaveExecutionSettingsAsync(new() { Body = JsonDocument.Parse(json).RootElement })).StatusCode);
        Assert.IsFalse(Directory.Exists(_directory));
    }

    [TestMethod]
    public async Task AppearancePersistsAcrossInstancesAndInvalidModesFail()
    {
        using (var terminal = Terminal())
        {
            Assert.AreEqual(400, (await terminal.SaveAppearanceSettingsAsync(Context(new { }))).StatusCode);
            Assert.AreEqual(400, (await terminal.SaveAppearanceSettingsAsync(Context(new { mode = "system" }))).StatusCode);
            Assert.AreEqual(200, (await terminal.SaveAppearanceSettingsAsync(Context(new { mode = "dark" }))).StatusCode);
        }
        using var restarted = Terminal();
        Assert.AreEqual("dark", Body(await restarted.GetAppearanceSettingsAsync(Context())).GetProperty("mode").GetString());
        Assert.AreEqual(200, (await restarted.SaveAppearanceSettingsAsync(Context(new { mode = "light" }))).StatusCode);
        using var again = Terminal();
        Assert.AreEqual("light", Body(await again.GetAppearanceSettingsAsync(Context())).GetProperty("mode").GetString());
        Assert.AreEqual(200, (await again.SaveAppearanceSettingsAsync(Context(new { mode = "host" }))).StatusCode);
        using var following = Terminal();
        Assert.AreEqual("host", Body(await following.GetAppearanceSettingsAsync(Context())).GetProperty("mode").GetString());
    }

    [TestMethod]
    public async Task CorruptFileReportsFailureAndCanBeReplacedExplicitly()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "appearance-settings.json"), "not json");
        await File.WriteAllTextAsync(Path.Combine(_directory, "execution-settings.json"), "{\"defaultHostAttempts\":99}");
        using var terminal = Terminal();
        Assert.AreEqual(503, (await terminal.GetAppearanceSettingsAsync(Context())).StatusCode);
        var execution = await terminal.GetExecutionSettingsAsync(Context());
        Assert.AreEqual(503, execution.StatusCode);
        Assert.AreEqual(1, Body(execution).GetProperty("defaultHostAttempts").GetInt32());
        Assert.AreEqual(200, (await terminal.SaveAppearanceSettingsAsync(Context(new { mode = "dark" }))).StatusCode);
        Assert.AreEqual(200, (await terminal.GetAppearanceSettingsAsync(Context())).StatusCode);
    }

    [TestMethod]
    public async Task FailedDiskWriteKeepsPreviousValueAndPolicyServiceFailureDoesNotSave()
    {
        using var terminal = Terminal();
        await terminal.SaveAppearanceSettingsAsync(Context(new { mode = "dark" }));
        var file = Path.Combine(_directory, "appearance-settings.json");
        File.Delete(file);
        Directory.CreateDirectory(file);
        Assert.AreEqual(503, (await terminal.SaveAppearanceSettingsAsync(Context(new { mode = "light" }))).StatusCode);
        Assert.AreEqual("dark", Body(await terminal.GetAppearanceSettingsAsync(Context())).GetProperty("mode").GetString());
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
        var host = PluginTestHost.Create("universalforward");
        Mock.Get(host).Setup(value => value.GetServiceAsync<IPluginPolicyRegistry>()).ThrowsAsync(new NotSupportedException());
        using var unsupported = Terminal(host);
        Assert.AreEqual(503, (await unsupported.SaveExecutionSettingsAsync(Context(new { defaultHostAttempts = 3 }))).StatusCode);
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "execution-settings.json")));
    }
}
