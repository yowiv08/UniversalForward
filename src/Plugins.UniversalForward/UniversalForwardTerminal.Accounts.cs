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
        try { policy.Validate(); } catch (FormatException error) { return context.BadRequest(error.Message); }
        var headerOverride = input.HeaderOverride ?? previous.HeaderOverride;
        try { HeaderOverrides.Validate(headerOverride); } catch (FormatException error) { return context.BadRequest(error.Message); }
        var settings = new ForwardApiSettings
        {
            HeaderOverride = headerOverride,
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

        var settings = new ForwardApiSettings
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            HeaderOverride = previous.HeaderOverride,
            ExtraParams = extraParams
        };

        try
        {
            var result = await FetchModelsAsync(settings, context.CancellationToken);
            if (result.Error is not null)
                return context.Json(result.StatusCode, new { error = result.Error });
            return context.Ok(new { models = result.Models, count = result.Models!.Length, updatedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return context.Json(502, new { error = SanitizeMessage(exception.Message, settings) });
        }
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
            var result = await FetchModelsAsync(settings, context.CancellationToken);
            if (result.Error is not null)
            {
                await TryLogAsync(
                    "models.refresh.failed",
                    result.Error,
                    "Error",
                    accountId: account.Id,
                    statusCode: result.StatusCode,
                    durationMs: (int)stopwatch.ElapsedMilliseconds);
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
                details: new { count = models.Length });
            return context.Ok(new { models, count = models.Length });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
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
            modelProtocols = settings.ModelProtocols,
            models = ReadModels(account),
            availableModels = ReadAvailableModels(account),
            modelsUpdatedAt = GetCredentialField(account, "modelsUpdatedAt"),
            state = account.Status.State.ToString()
        };
    }
    private async Task<(string[]? Models, int StatusCode, string? Error)> FetchModelsAsync(
        ForwardApiSettings settings,
        CancellationToken cancellationToken)
    {
        if (!TryReadReplaceHeaders(ReadExtraParams(settings), out var replaceHeaders, out var error))
            return (null, 400, error);
        using var client = _host.Http.CreateDirectClient(new PluginHttpClientOptions { AllowAutoRedirect = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings.BaseUrl, "/v1/models"));
        ApplyApiKeyHeader(request, settings);
        ApplyReplaceHeaders(request, replaceHeaders);
        ApplyReplaceHeaders(request, HeaderOverrides.Resolve(settings.HeaderOverride, new Dictionary<string, string>(), settings.ApiKey, channelTest: true));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (null, (int)response.StatusCode, DescribeResponseError(response.StatusCode, responseText, settings));

        string[] models;
        try
        {
            models = ParseModelIds(responseText);
        }
        catch (JsonException)
        {
            return (null, 502, "上游模型列表不是有效的 JSON");
        }

        return models.Length == 0
            ? (null, 502, "上游返回成功，但响应中没有可用模型（data[].id）")
            : (models, (int)response.StatusCode, null);
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
        public Dictionary<string, ModelProtocolOptions>? ModelProtocols { get; init; }
    }
    private sealed class ModelDiscoveryInput
    {
        public ModelDiscoveryInput() { }
        public string? Id { get; init; }
        public string? BaseUrl { get; init; }
        public string? ApiKey { get; init; }
        public string? KeyId { get; init; }
        public JsonElement? ExtraParams { get; init; }
    }
    private sealed class AccountIdInput
    {
        public AccountIdInput() { }
        public string? Id { get; init; }
        public string? KeyId { get; init; }
    }
}
