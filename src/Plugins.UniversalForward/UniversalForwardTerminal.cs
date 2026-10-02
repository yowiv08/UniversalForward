using System.Text.Json;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

/// <summary>渠道管理、模型目录与上游转发。</summary>
[PipelinePlugin(PipelineStage.Terminal, 100, "UniversalForwardTerminal")]
[PlatformAdapter("universalforward", PluginKey = "universalforward", DisplayName = "UniversalForward")]
[ModelCache(Disabled = true)]
[CredentialSchema(CredentialKind.Custom)]
public sealed partial class UniversalForwardTerminal(IPluginHost host)
    : IPlatformTerminal, IPluginModule, IPluginMainPageProvider, IDisposable
{
    private const string ForwardApiPlatform = "universalforward";
    private const string ModelCacheKeyPrefix = "models:v1";
    private static readonly TimeSpan ModelCacheTtl = TimeSpan.FromDays(30);
    internal static readonly string[] SupportedEndpoints =
    [
        "/v1/responses",
        "/v1/messages"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPluginServices _host = host.Services;
    private readonly SemaphoreSlim _snapshotRefreshGate = new(1, 1);
    private AccountModelSnapshot _accountModelSnapshot = AccountModelSnapshot.Empty;
    public PluginMainPage GetMainPage() => CreateMainPage();
    public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(
        ModelQueryContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref _accountModelSnapshot);
        var useFreshSnapshot = context.ForceRefresh || !snapshot.IsLoaded;
        if (context.ForceRefresh)
            snapshot = await RebuildAccountModelSnapshotAsync(_host.PluginKey, cancellationToken, forceRefresh: true);
        else if (!snapshot.IsLoaded)
            snapshot = await RebuildAccountModelSnapshotAsync(_host.PluginKey, cancellationToken, forceRefresh: false);

        var models = useFreshSnapshot
            ? snapshot.Models
            : await ReadCachedModelsAsync(ForwardApiPlatform, cancellationToken) ?? snapshot.Models;
        return models
            .Select(model => new ModelDescriptor(model, model))
            .ToArray();
    }
    public Task<CredentialValidationResult> ValidateCredentialAsync(
        Credential credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSettings(credential, out var settings))
            return Task.FromResult(new CredentialValidationResult(false, "UniversalForward account settings are missing or invalid"));
        if (settings.Keys is null && string.IsNullOrWhiteSpace(settings.ApiKey))
            return Task.FromResult(new CredentialValidationResult(false, "API key is empty"));
        if (!TryValidateBaseUrl(settings.BaseUrl, out var error))
            return Task.FromResult(new CredentialValidationResult(false, error));
        if (!TryReadReplaceHeaders(ReadExtraParams(settings), out _, out var headerError))
            return Task.FromResult(new CredentialValidationResult(false, headerError));
        return Task.FromResult(new CredentialValidationResult(true));
    }
    public void Configure(IPluginBuilder builder)
    {
        builder.Job(new PluginJobRegistration("connection-test", ForwardApiPlatform,
            RunConnectionTestsAsync, TimeSpan.FromHours(1), "串行测试渠道模型连接"));
        builder.AccountPolicy(policy => policy
            .SelectForRequest((account, request) => IsAccountEligible(account, request))
            .WeightBy((account, _) => GetAccountModelPolicy(account)?.Weight ?? int.MinValue));

        builder.ProxyPolicy(policy => policy
            .OnTransportFailure(() => new PluginAttemptDecision { Retry = PluginRetryAction.None, AccountAction = PluginAccountAction.None, ProxyAction = PluginProxyAction.None })
            .MaxAttempts(1)
            .AttemptTimeoutSeconds(3600)
            .TotalTimeoutSeconds(3600));
    }
    public async ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
    {
        InitializeRequestLogs();
        await RebuildAccountModelSnapshotAsync(context.PluginKey, cancellationToken);
    }
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
    public void Dispose() { DisposeRequestLogs(); _keySelector.Clear(); _snapshotRefreshGate.Dispose(); }

    private readonly ChannelKeySelector _keySelector = new();
    private static List<ChannelKey> ReadKeys(ForwardApiSettings settings) => ChannelKeys.Read(settings.Keys, settings.ApiKey);
    private bool IsAccountEligible(Account account, AdapterRequest request)
    {
        if (!SupportedEndpoints.Contains(NormalizeEndpoint(request.Endpoint), StringComparer.OrdinalIgnoreCase))
            return false;
        var policy = GetAccountModelPolicy(account);
        if (policy is null || !policy.Enabled)
            return false;
        if (!policy.Endpoints.Contains(NormalizeEndpoint(request.Endpoint), StringComparer.OrdinalIgnoreCase))
            return false;
        return policy.Models.Contains(request.Model, StringComparer.OrdinalIgnoreCase)
            || policy.Models.Contains(UpstreamModel(request.Model), StringComparer.OrdinalIgnoreCase);
    }
    private AccountModelPolicy? GetAccountModelPolicy(Account account)
    {
        var snapshot = Volatile.Read(ref _accountModelSnapshot);
        if (snapshot.Accounts.TryGetValue(account.Id, out var cached))
            return cached;
        if (!TryReadSettings(account, out var settings))
            return null;
        return new AccountModelPolicy(settings.Enabled && ReadKeys(settings).Any(k => k.Enabled), settings.Weight, settings.Endpoints, ReadModels(account));
    }
    private async Task<AccountModelSnapshot> RebuildAccountModelSnapshotAsync(
        string pluginKey,
        CancellationToken cancellationToken,
        bool forceRefresh = true)
    {
        await _snapshotRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref _accountModelSnapshot);
            if (!forceRefresh && current.IsLoaded) return current;

            var accounts = await _host.Accounts.ListAsync(ForwardApiPlatform, cancellationToken);
            var policies = new Dictionary<string, AccountModelPolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in accounts)
            {
                if (!TryReadSettings(account, out var settings)) continue;
                policies[account.Id] = new AccountModelPolicy(
                    settings.Enabled && ReadKeys(settings).Any(k => k.Enabled) && account.Status.State is not (ResourceState.Disabled or ResourceState.Invalid),
                    settings.Weight,
                    settings.Endpoints,
                    ReadModels(account));
            }

            var models = policies.Values
                .Where(policy => policy.Enabled)
                .SelectMany(policy => policy.Models)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var snapshot = new AccountModelSnapshot(true, policies, models);
            Volatile.Write(ref _accountModelSnapshot, snapshot);
            await StoreCachedModelsAsync(ForwardApiPlatform, models, cancellationToken);
            return snapshot;
        }
        finally
        {
            _snapshotRefreshGate.Release();
        }
    }
    private async Task<string[]?> ReadCachedModelsAsync(string platform, CancellationToken cancellationToken)
    {
        try
        {
            var cache = _host.State.Shared;
            if (!cache.IsAvailable) return null;
            var value = await cache.GetStringAsync(GetModelCacheKey(platform), cancellationToken);
            return string.IsNullOrWhiteSpace(value)
                ? null
                : JsonSerializer.Deserialize<string[]>(value, JsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
    private async Task StoreCachedModelsAsync(
        string platform,
        string[] models,
        CancellationToken cancellationToken)
    {
        try
        {
            var cache = _host.State.Shared;
            if (cache.IsAvailable)
                await cache.SetStringAsync(
                    GetModelCacheKey(platform),
                    JsonSerializer.Serialize(models, JsonOptions),
                    ModelCacheTtl,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
    private static string GetModelCacheKey(string platform)
        => $"{ModelCacheKeyPrefix}:{platform}";
    private static string NormalizeEndpoint(string endpoint)
        => "/" + endpoint.Trim().Trim('/');
    private static bool TryReadSettings(Account account, out ForwardApiSettings settings)
        => TryReadSettings(account.Credential, out settings);
    private static bool TryReadSettings(Credential credential, out ForwardApiSettings settings)
    {
        settings = new ForwardApiSettings();
        if (credential is not CustomCredential custom
            || !custom.Fields.TryGetValue("settings", out var json)
            || string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            settings = JsonSerializer.Deserialize<ForwardApiSettings>(json, JsonOptions) ?? new ForwardApiSettings();
            if (settings.RequestPolicy is null || settings.Endpoints is null) return false;
            settings.RequestPolicy.Validate();
            ValidateNetworkMode(settings.NetworkMode);
            ChannelKeys.Validate(ReadKeys(settings), settings.KeySelectionMode);
            HeaderOverrides.ValidateConfiguration(settings.HeaderOverrideMode, settings.HeaderOverride, settings.EndpointHeaderOverrides);
            if (settings.ModelProtocols is null) return false;
            foreach (var options in settings.ModelProtocols.Values)
            { if (options is null) return false; options.Validate(); }
            settings.Endpoints = settings.Endpoints
                .Select(NormalizeEndpoint)
                .Where(endpoint => SupportedEndpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return false;
        }
    }
    private static string[] ReadModels(Account account)
        => HasConfiguredModels(account) ? ReadModelList(account, "models") : [];
    private static string[] ReadAvailableModels(Account account)
    {
        var available = ReadModelList(account, "availableModels");
        return available.Length > 0 || HasConfiguredModels(account)
            ? available
            : ReadModelList(account, "models");
    }
    private static bool HasConfiguredModels(Account account)
        => account.Credential is CustomCredential custom
            && custom.Fields.TryGetValue("modelsConfigured", out var configured)
            && string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase);
    private static string[] ReadModelList(Account account, string fieldName)
    {
        if (account.Credential is not CustomCredential custom
            || !custom.Fields.TryGetValue(fieldName, out var json)
            || string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions)?
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
    private static bool TryValidateBaseUrl(string? value, out string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Base URL must be an absolute http(s) URL without credentials, query, or fragment";
            return false;
        }

        error = string.Empty;
        return true;
    }
    private sealed class ForwardApiSettings
    {
        public ForwardApiSettings() { }
        public string BaseUrl { get; set; } = string.Empty;
        public string NetworkMode { get; set; } = "direct";
        public string ApiKey { get; set; } = string.Empty;
        public List<ChannelKey>? Keys { get; set; }
        public string KeySelectionMode { get; set; } = "roundRobin";
        public long KeyRevision { get; set; }
        public int Weight { get; set; }
        public string[] Endpoints { get; set; } = SupportedEndpoints.ToArray();
        public bool Enabled { get; set; } = true;
        public string ExtraParams { get; set; } = "{}";
        public ForwardRequestPolicy RequestPolicy { get; set; } = new();
        public System.Text.Json.Nodes.JsonObject HeaderOverride { get; set; } = new();
        public string HeaderOverrideMode { get; set; } = "shared";
        public Dictionary<string, EndpointHeaderOverride> EndpointHeaderOverrides { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModelProtocolOptions> ModelProtocols { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed record AccountModelPolicy(bool Enabled, int Weight, string[] Endpoints, string[] Models);
    private sealed record AccountModelSnapshot(
        bool IsLoaded,
        IReadOnlyDictionary<string, AccountModelPolicy> Accounts,
        string[] Models)
    {
        public static AccountModelSnapshot Empty { get; } = new(
            false,
            new Dictionary<string, AccountModelPolicy>(StringComparer.OrdinalIgnoreCase),
            []);
    }
}
