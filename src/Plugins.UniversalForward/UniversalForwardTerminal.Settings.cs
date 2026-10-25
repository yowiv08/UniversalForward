using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    internal string SettingsDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, "data", ForwardApiPlatform);
    private PluginSettingsStore? _settings;
    private PluginSettingsStore Settings => LazyInitializer.EnsureInitialized(ref _settings, () => new(SettingsDirectory));
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    private object ExecutionLimits() => new
    {
        setupTimeoutSeconds = Math.Min(300, _host.Execution.SetupTimeout.TotalSeconds),
        totalSetupTimeoutSeconds = Math.Min(180, _host.Execution.TotalSetupTimeout.TotalSeconds),
        responseTimeoutSeconds = _host.Execution.ResponseTimeout.TotalSeconds
    };

    [PluginEndpoint("GET", "execution/settings")]
    public Task<PluginResult> GetExecutionSettingsAsync(PluginHttpContext context)
        => Task.FromResult(context.Json(Settings.Execution.LoadError is null ? 200 : 503, new
        {
            Settings.Execution.Value.DefaultHostAttempts,
            execution = ExecutionLimits(), error = Settings.Execution.LoadError
        }));

    [PluginEndpoint("POST", "execution/settings")]
    public async Task<PluginResult> SaveExecutionSettingsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<ExecutionSettings>(context.Body, out var input)) return context.BadRequest("尝试次数设置无效。");
        try { input.Validate(); }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        await _settingsGate.WaitAsync(context.CancellationToken);
        try
        {
            // 使用公开 Contracts 注册表，平台快照和包默认值都必须更新。
            var registry = await host.GetServiceAsync<IPluginPolicyRegistry>().WaitAsync(context.CancellationToken)
                ?? throw new NotSupportedException("宿主未提供插件策略服务。");
            var package = registry.GetSnapshot(_host.PluginKey);
            var platform = registry.GetSnapshot(_host.PluginKey, ForwardApiPlatform);
            await Settings.Execution.SaveAsync(input, context.CancellationToken);
            registry.Set(_host.PluginKey, package.Attempts with { MaxAttempts = input.DefaultHostAttempts }, package.Accounts);
            registry.Set(_host.PluginKey, ForwardApiPlatform,
                platform.Attempts with { MaxAttempts = input.DefaultHostAttempts }, platform.Accounts);
            return context.Ok(new { input.DefaultHostAttempts, execution = ExecutionLimits() });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        { return context.Json(503, new { error = "尝试次数保存失败，请检查插件数据目录权限和宿主策略服务。" }); }
        finally { _settingsGate.Release(); }
    }

    [PluginEndpoint("GET", "appearance/settings")]
    public Task<PluginResult> GetAppearanceSettingsAsync(PluginHttpContext context)
        => Task.FromResult(Settings.Appearance.LoadError is { } error
            ? context.Json(503, new { error }) : context.Ok(Settings.Appearance.Value));

    [PluginEndpoint("POST", "appearance/settings")]
    public async Task<PluginResult> SaveAppearanceSettingsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AppearanceSettings>(context.Body, out var input)) return context.BadRequest("外观设置无效。");
        try { input.Validate(); }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        await _settingsGate.WaitAsync(context.CancellationToken);
        try
        {
            await Settings.Appearance.SaveAsync(input, context.CancellationToken);
            return context.Ok(input);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return context.Json(503, new { error = "外观保存失败，请检查插件数据目录权限。" }); }
        finally { _settingsGate.Release(); }
    }
}
