using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    [PluginEndpoint("GET", "accounts")]
    public async Task<PluginResult> ListAccountsAsync(PluginHttpContext context)
    {
        var accounts = await _host.Accounts.ListAsync(
            context.Platform,
            context.CancellationToken);
        return context.Ok(new { accounts = accounts.Select(ToAccountCard).ToArray() });
    }
    [PluginEndpoint("POST", "accounts/save")]
    public async Task<PluginResult> SaveAccountAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountSaveInput>(context.Body, out var input))
            return context.BadRequest("请求内容无效");

        Account? existing = null;
        if (!string.IsNullOrWhiteSpace(input.Id))
        {
            existing = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
            if (existing is null) return context.Json(404, new { error = "账号不存在" });
        }

        var credentialVersion = existing?.CredentialVersion ?? 0;
        var previous = existing is not null && TryReadSettings(existing, out var oldSettings)
            ? oldSettings
            : new ForwardApiSettings();

        var baseUrl = (input.BaseUrl ?? previous.BaseUrl).Trim().TrimEnd('/');
        if (!TryValidateBaseUrl(baseUrl, out var urlError))
            return context.BadRequest(urlError);

        if (context.Body is JsonElement raw && raw.EnumerateObject().Any(p => p.Name.Equals("apiKey", StringComparison.OrdinalIgnoreCase))
            && raw.EnumerateObject().Any(p => p.Name.Equals("keys", StringComparison.OrdinalIgnoreCase))) return context.BadRequest("不能同时提交 apiKey 和 keys");
        var oldKeys = ReadKeys(previous);
        var keyEdit = input.Keys is not null || input.DeletedKeyIds is not null || input.KeySelectionMode is not null
            || !string.IsNullOrWhiteSpace(input.ApiKey);
        if (existing is not null && keyEdit && input.KeyRevision != previous.KeyRevision)
            return context.Json(409, new { error = "Key 配置已变化，请重新加载后编辑" });
        List<ChannelKey> keys;
        var mode = input.KeySelectionMode ?? previous.KeySelectionMode;
        try
        {
            if (!string.IsNullOrWhiteSpace(input.ApiKey))
            {
                if (previous.Keys is not null) return context.BadRequest("多 Key 渠道请使用 keys 编辑");
                keys = [new ChannelKey { Id = "legacy", Name = "默认 Key", Secret = input.ApiKey.Trim() }];
            }
            else keys = ChannelKeys.Merge(oldKeys, input.Keys, input.DeletedKeyIds);
            ChannelKeys.Validate(keys, mode);
        }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        var keysChanged = previous.Keys is null || mode != previous.KeySelectionMode || !keys.SequenceEqual(oldKeys);

        var label = input.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
            return context.BadRequest("账号名称不能为空");

        var endpoints = (input.Endpoints ?? previous.Endpoints)
            .Select(NormalizeEndpoint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (endpoints.Length == 0
            || endpoints.Any(endpoint => !SupportedEndpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase)))
            return context.BadRequest("至少选择一个有效的请求端点");

        if (!TryReadExtraParams(input.ExtraParams, previous.ExtraParams, out var extraParams, out var extraError))
            return context.BadRequest(extraError);

        var policy = input.RequestPolicy ?? previous.RequestPolicy;
        var networkMode = input.NetworkMode ?? previous.NetworkMode;
        try { ValidateNetworkMode(networkMode); }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        try { policy.Validate(); } catch (FormatException error) { return context.BadRequest(error.Message); }
        var headerOverride = input.HeaderOverride ?? previous.HeaderOverride;
        var headerOverrideMode = input.HeaderOverrideMode ?? previous.HeaderOverrideMode;
        var endpointHeaderOverrides = input.EndpointHeaderOverrides ?? previous.EndpointHeaderOverrides;
        try { HeaderOverrides.ValidateConfiguration(headerOverrideMode, headerOverride, endpointHeaderOverrides); }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        var settings = new ForwardApiSettings
        {
            HeaderOverride = headerOverride,
            NetworkMode = networkMode,
            HeaderOverrideMode = headerOverrideMode,
            EndpointHeaderOverrides = endpointHeaderOverrides,
            ModelProtocols = input.ModelProtocols ?? previous.ModelProtocols,
            RequestPolicy = policy,
            BaseUrl = baseUrl,
            Keys = keys,
            KeySelectionMode = mode,
            KeyRevision = keysChanged ? checked(previous.KeyRevision + 1) : previous.KeyRevision,
            Weight = Math.Clamp(input.Weight ?? previous.Weight, 0, 1000),
            Endpoints = endpoints,
            Enabled = input.Enabled ?? previous.Enabled,
            ExtraParams = extraParams
        };

        var credentialsChanged = existing is null
            || !string.Equals(baseUrl, previous.BaseUrl, StringComparison.OrdinalIgnoreCase)
            || keysChanged
            || !string.Equals(extraParams, previous.ExtraParams, StringComparison.Ordinal);
        var models = input.Models is null
            ? existing is null ? [] : ReadModels(existing)
            : input.Models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (models.Length == 0)
            return context.BadRequest("请手填或选择至少一个允许使用的模型");

        try
        {
            foreach (var (model, options) in settings.ModelProtocols)
            {
                if (!models.Contains(model, StringComparer.Ordinal) || options is null)
                    return context.BadRequest("协议配置必须引用当前允许模型");
                options.Validate();
            }
        }
        catch (FormatException error) { return context.BadRequest(error.Message); }

        var discoveredModels = input.AvailableModels?
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fields = existing?.Credential is CustomCredential custom
            ? new Dictionary<string, string?>(custom.Fields, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);
        if (credentialsChanged)
        {
            fields.Remove("availableModels");
            fields.Remove("modelsUpdatedAt");
        }
        if (discoveredModels is not null)
        {
            fields["availableModels"] = JsonSerializer.Serialize(discoveredModels, JsonOptions);
            fields["modelsUpdatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        }
        fields["models"] = JsonSerializer.Serialize(models, JsonOptions);
        fields["modelsConfigured"] = "true";
        fields["settings"] = JsonSerializer.Serialize(settings, JsonOptions);
        var account = new Account
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            PluginKey = context.PluginKey, Platform = context.Platform, Label = label,
            Credential = new CustomCredential(fields), Status = existing?.Status ?? new ResourceStatus()
        };
        Account? saved;
        string? warning = null;
        if (existing is null) saved = await _host.Accounts.SaveAsync(account, context.CancellationToken);
        else
        {
            saved = await _host.Accounts.CompareExchangeCredentialAsync(existing.Id, credentialVersion,
                account.Credential, context.CancellationToken);
            if (saved is null) return context.Json(409, new { error = "渠道凭据已变化，请重新加载后保存" });
            if (existing.Label != label)
            {
                try { saved = await _host.Accounts.PatchAsync(account, ["label"], context.CancellationToken)
                    ?? throw new InvalidOperationException("渠道名称未更新"); }
                catch (Exception) { warning = "凭据已保存，但渠道名称更新失败，请重新加载后重试名称修改"; }
            }
        }
        if (existing is not null && (mode != previous.KeySelectionMode
            || !keys.Select(k => (k.Id, k.Enabled)).SequenceEqual(oldKeys.Select(k => (k.Id, k.Enabled)))))
            _keySelector.Remove(existing.Id);
        await RebuildAccountModelSnapshotAsync(context.PluginKey, context.CancellationToken);
        _host.Models.Invalidate(ForwardApiPlatform);
        await TryLogAsync(
            "account.saved",
            $"账号“{label}”配置已保存",
            accountId: saved.Id,
            details: new { settings.Weight, settings.Endpoints, settings.Enabled });
        return context.Ok(new { account = ToAccountCard(saved), warning });
    }
    [PluginEndpoint("POST", "models/discover")]
    public async Task<PluginResult> DiscoverModelsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<ModelDiscoveryInput>(context.Body, out var input))
            return context.BadRequest("请求内容无效");

        Account? existing = null;
        if (!string.IsNullOrWhiteSpace(input.Id))
        {
            existing = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
            if (existing is null) return context.Json(404, new { error = "账号不存在" });
        }

        var previous = existing is not null && TryReadSettings(existing, out var oldSettings)
            ? oldSettings
            : new ForwardApiSettings();

        var baseUrl = (input.BaseUrl ?? previous.BaseUrl).Trim().TrimEnd('/');
        if (!TryValidateBaseUrl(baseUrl, out var urlError)) return context.BadRequest(urlError);

        string apiKey;
        try { apiKey = !string.IsNullOrWhiteSpace(input.ApiKey) ? input.ApiKey.Trim()
            : _keySelector.Select(existing?.Id ?? "", ReadKeys(previous), previous.KeySelectionMode, input.KeyId, discovery: true).Secret; }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        if (string.IsNullOrWhiteSpace(apiKey)) return context.BadRequest("API Key 不能为空");
        if (!TryReadExtraParams(input.ExtraParams, previous.ExtraParams, out var extraParams, out var extraError))
            return context.BadRequest(extraError);

        var headerOverride = input.HeaderOverride ?? previous.HeaderOverride;
        var networkMode = input.NetworkMode ?? previous.NetworkMode;
        try { ValidateNetworkMode(networkMode); }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        try { HeaderOverrides.Validate(headerOverride); }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        var settings = new ForwardApiSettings
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            NetworkMode = networkMode,
            HeaderOverride = headerOverride,
            ExtraParams = extraParams
        };

        try
        {
            var result = await FetchModelsAsync(settings, "models-discover", existing?.Id, existing?.Label, context.CancellationToken);
            if (result.Error is not null)
            {
                await TryLogAsync("models.discover.failed", result.Error, "Error",
                    statusCode: result.StatusCode, details: new { response = result.Error });
                return context.Json(result.StatusCode, new { error = result.Error });
            }
            return context.Ok(new { models = result.Models, count = result.Models!.Length, networkMode, updatedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProxyPoolUnavailableException)
        {
            await TryLogAsync("models.discover.failed", ProxyUnavailableMessage, "Error", statusCode: 503,
                details: new { networkMode, code = "proxy_pool_unavailable" });
            return context.Json(503, new { error = ProxyUnavailableMessage, code = "proxy_pool_unavailable", networkMode });
        }
        catch (Exception exception)
        {
            return context.Json(502, new { error = SanitizeMessage(exception.Message, settings) });
        }
    }
    [PluginEndpoint("POST", "accounts/enabled")]
    public async Task<PluginResult> SetAccountEnabledAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountSaveInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id) || input.Enabled is null)
            return context.BadRequest("请指定渠道 ID 和启用状态");
        var existing = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
        if (existing is null) return context.Json(404, new { error = "账号不存在" });
        if (!TryReadSettings(existing, out _) || existing.Credential is not CustomCredential custom)
            return context.BadRequest("渠道配置无效");
        var fields = new Dictionary<string, string?>(custom.Fields, StringComparer.Ordinal);
        if (JsonNode.Parse(fields["settings"]!) is not JsonObject settings)
            return context.BadRequest("渠道配置无效");
        foreach (var name in settings.Select(x => x.Key).Where(x => x.Equals("enabled", StringComparison.OrdinalIgnoreCase)).ToArray())
            settings.Remove(name);
        settings["enabled"] = input.Enabled.Value;
        fields["settings"] = settings.ToJsonString();
        var saved = await _host.Accounts.CompareExchangeCredentialAsync(existing.Id, existing.CredentialVersion,
            new CustomCredential(fields), context.CancellationToken);
        if (saved is null) return context.Json(409, new { error = "渠道配置已变化，请刷新后重试" });
        await RebuildAccountModelSnapshotAsync(context.PluginKey, context.CancellationToken);
        _host.Models.Invalidate(ForwardApiPlatform);
        await TryLogAsync("account.enabled", $"渠道“{saved.Label ?? saved.Id}”已{(input.Enabled.Value ? "启用" : "禁用")}",
            accountId: saved.Id, details: new { enabled = input.Enabled.Value });
        return context.Ok(new { account = ToAccountCard(saved) });
    }

    [PluginEndpoint("POST", "accounts/delete")]
    public async Task<PluginResult> DeleteAccountAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountIdInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("账号 ID 不能为空");
        var account = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
        if (account is null) return context.Json(404, new { error = "账号不存在" });
        await _host.Accounts.DeleteAsync(account.Id, context.CancellationToken);
        _keySelector.Remove(account.Id);
        await RebuildAccountModelSnapshotAsync(context.PluginKey, context.CancellationToken);
        _host.Models.Invalidate(ForwardApiPlatform);
        await TryLogAsync("account.deleted", $"账号“{account.Label ?? account.Id}”已删除", accountId: account.Id);
        return context.Ok(new { deleted = true });
    }
    [PluginEndpoint("POST", "models/refresh")]
    public async Task<PluginResult> RefreshModelsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountIdInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("账号 ID 不能为空");
        var account = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
        if (account is null) return context.Json(404, new { error = "账号不存在" });
        if (!TryReadSettings(account, out var settings))
            return context.BadRequest("账号配置无效");

        try { settings.ApiKey = _keySelector.Select(account.Id, ReadKeys(settings), settings.KeySelectionMode,
            input.KeyId, discovery: true).Secret; }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        var credentialVersion = account.CredentialVersion;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await FetchModelsAsync(settings, "models-refresh", account.Id, account.Label, context.CancellationToken);
            if (result.Error is not null)
            {
                await TryLogAsync(
                    "models.refresh.failed",
                    result.Error,
                    "Error",
                    accountId: account.Id,
                    statusCode: result.StatusCode,
                    durationMs: (int)stopwatch.ElapsedMilliseconds,
                    details: new { response = result.Error });
                return context.Json(result.StatusCode, new { error = result.Error });
            }

            var models = result.Models!;

            var fields = CopyCredentialFields(account);
            fields["availableModels"] = JsonSerializer.Serialize(models, JsonOptions);
            fields["modelsUpdatedAt"] = DateTimeOffset.UtcNow.ToString("O");
            var updated = await _host.Accounts.CompareExchangeCredentialAsync(account.Id, credentialVersion,
                new CustomCredential(fields), context.CancellationToken);
            if (updated is null) return context.Json(409, new { error = "渠道凭据已变化，请重新刷新模型" });
            await TryLogAsync(
                "models.refresh.succeeded",
                $"成功拉取 {models.Length} 个模型",
                accountId: account.Id,
                statusCode: result.StatusCode,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                details: new { count = models.Length, networkMode = settings.NetworkMode });
            return context.Ok(new { models, count = models.Length, networkMode = settings.NetworkMode });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProxyPoolUnavailableException)
        {
            await TryLogAsync("models.refresh.failed", ProxyUnavailableMessage, "Error", accountId: account.Id, statusCode: 503,
                details: new { networkMode = settings.NetworkMode, code = "proxy_pool_unavailable" });
            return context.Json(503, new { error = ProxyUnavailableMessage, code = "proxy_pool_unavailable", networkMode = settings.NetworkMode });
        }
        catch (Exception exception)
        {
            var error = SanitizeMessage(exception.Message, settings);
            await TryLogAsync(
                "models.refresh.failed",
                error,
                "Error",
                accountId: account.Id,
                durationMs: (int)stopwatch.ElapsedMilliseconds);
            return context.Json(502, new { error });
        }
    }
    private static object ToAccountCard(Account account)
    {
        var hasSettings = TryReadSettings(account, out var settings);
        var keys = hasSettings ? ReadKeys(settings) : [];
        var key = keys.FirstOrDefault(k => k.Enabled)?.Secret ?? "";
        return new
        {
            id = account.Id,
            label = account.Label ?? string.Empty,
            baseUrl = settings.BaseUrl,
            apiKeyMasked = MaskSecret(key),
            keySelectionMode = settings.KeySelectionMode, keyRevision = settings.KeyRevision,
            enabledKeyCount = keys.Count(k => k.Enabled), totalKeyCount = keys.Count,
            keys = keys.Select(k => new { k.Id, k.Name, masked = MaskSecret(k.Secret), k.Enabled }).ToArray(),
            hasApiKey = !string.IsNullOrWhiteSpace(key),
            weight = settings.Weight,
            endpoints = settings.Endpoints,
            enabled = settings.Enabled,
            extraParams = settings.ExtraParams,
            requestPolicy = settings.RequestPolicy,
            headerOverride = settings.HeaderOverride,
            headerOverrideMode = settings.HeaderOverrideMode,
            networkMode = settings.NetworkMode,
            endpointHeaderOverrides = settings.EndpointHeaderOverrides,
            modelProtocols = settings.ModelProtocols,
            models = ReadModels(account),
            availableModels = ReadAvailableModels(account),
            modelsUpdatedAt = GetCredentialField(account, "modelsUpdatedAt"),
            state = account.Status.State.ToString()
        };
    }
    private async Task<(string[]? Models, int StatusCode, string? Error)> FetchModelsAsync(
        ForwardApiSettings settings,
        string kind, string? accountId, string? label, CancellationToken cancellationToken)
    {
        var capture = BeginRequestLog(store => store.Begin(kind, accountId, label, null, "/v1/models", settings.NetworkMode, null, null, null, null));
        try
        {
            var result = await FetchModelsCoreAsync(settings, capture, cancellationToken);
            capture?.Complete(result.Error is null ? "completed" : "failed", result.StatusCode, result.Error);
            return result;
        }
        catch (Exception error)
        {
            capture?.Complete(error is OperationCanceledException ? "cancelled" : "failed", error: error.Message);
            throw;
        }
    }
    private async Task<(string[]? Models, int StatusCode, string? Error)> FetchModelsCoreAsync(
        ForwardApiSettings settings, RequestLogCapture? capture, CancellationToken cancellationToken)
    {
        if (!TryReadReplaceHeaders(ReadExtraParams(settings), out var replaceHeaders, out var error))
            return (null, 400, error);
        using var client = await CreateNetworkClientAsync(settings.NetworkMode, true, cancellationToken);
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings.BaseUrl, "/v1/models"));
        ApplyApiKeyHeader(request, settings);
        ApplyReplaceHeaders(request, replaceHeaders);
        ApplyReplaceHeaders(request, HeaderOverrides.Resolve(settings.HeaderOverride, new Dictionary<string, string>(), settings.ApiKey, channelTest: true));
        var logAttempt = capture?.Sending(request, []) ?? 0;
        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            capture?.Received(logAttempt, response);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (null, (int)response.StatusCode, ModelResponseDiagnostic(settings, request, response, responseText));

            string[] models;
            try
            {
                models = ParseModelIds(responseText);
            }
            catch (JsonException)
            {
                return (null, 502, ModelResponseDiagnostic(settings, request, response, responseText));
            }

            return models.Length == 0
                ? (null, 502, ModelResponseDiagnostic(settings, request, response, responseText,
                    "响应中没有可用模型（data[].id 或 models[].id/name）"))
                : (models, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            capture?.SendError(logAttempt, exception);
            return (null, 502, ModelResponseDiagnostic(settings, request, response, null,
                $"{exception.GetType().Name}: {exception.Message}"));
        }
        finally { response?.Dispose(); }
    }
    private static string MaskSecret(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length <= 6 ? "••••••" : $"••••••{value[^4..]}";
    private static bool TryReadBody<T>(object? body, out T input) where T : class
    {
        try
        {
            if (body is JsonElement { ValueKind: JsonValueKind.Object } element
                && JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions) is { } value)
            {
                input = value;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        input = null!;
        return false;
    }
    private static bool TryReadExtraParams(
        JsonElement? requested,
        string fallback,
        out string value,
        out string error)
    {
        value = fallback;
        error = "extraParams 必须是 JSON 对象";
        if (requested is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            try
            {
                return JsonNode.Parse(fallback) is JsonObject existing
                    && TryReadReplaceHeaders(existing, out _, out error);
            }
            catch (JsonException) { return false; }
        }

        try
        {
            var json = element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText();
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            value = document.RootElement.GetRawText();
            return TryReadReplaceHeaders(JsonNode.Parse(value)!.AsObject(), out _, out error);
        }
        catch (JsonException)
        {
            return false;
        }
    }
    private static Dictionary<string, string?> CopyCredentialFields(Account account)
        => account.Credential is CustomCredential custom
            ? new Dictionary<string, string?>(custom.Fields, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);
    private static string? GetCredentialField(Account account, string name)
        => account.Credential is CustomCredential custom
            && custom.Fields.TryGetValue(name, out var value)
                ? value
                : null;
    private sealed class AccountSaveInput
    {
        public AccountSaveInput() { }
        public string? NetworkMode { get; init; }
        public List<ChannelKey>? Keys { get; init; }
        public string[]? DeletedKeyIds { get; init; }
        public string? KeySelectionMode { get; init; }
        public long? KeyRevision { get; init; }
        public string? Id { get; init; }
        public string? Label { get; init; }
        public string? BaseUrl { get; init; }
        public string? ApiKey { get; init; }
        public string? KeyId { get; init; }
        public int? Weight { get; init; }
        public string[]? Endpoints { get; init; }
        public string[]? Models { get; init; }
        public string[]? AvailableModels { get; init; }
        public bool? Enabled { get; init; }
        public JsonElement? ExtraParams { get; init; }
        public ForwardRequestPolicy? RequestPolicy { get; init; }
        public JsonObject? HeaderOverride { get; init; }
        public string? HeaderOverrideMode { get; init; }
        public Dictionary<string, EndpointHeaderOverride>? EndpointHeaderOverrides { get; init; }
        public Dictionary<string, ModelProtocolOptions>? ModelProtocols { get; init; }
    }
    private sealed class ModelDiscoveryInput
    {
        public ModelDiscoveryInput() { }
        public string? NetworkMode { get; init; }
        public string? Id { get; init; }
        public string? BaseUrl { get; init; }
        public string? ApiKey { get; init; }
        public string? KeyId { get; init; }
        public JsonElement? ExtraParams { get; init; }
        public JsonObject? HeaderOverride { get; init; }
    }
    private sealed class AccountIdInput
    {
        public AccountIdInput() { }
        public string? Id { get; init; }
        public string? KeyId { get; init; }
    }
}
