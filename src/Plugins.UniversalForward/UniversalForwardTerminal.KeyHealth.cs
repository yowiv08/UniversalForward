using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    private readonly KeyHealthTracker _keyHealth = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyHealthGates = new(StringComparer.Ordinal);
    private SemaphoreSlim KeyHealthGate(string channel) => _keyHealthGates.GetOrAdd(channel, _ => new(1, 1));

    [PluginEndpoint("POST", "accounts/keys/disable")]
    public async Task<PluginResult> DisableAccountKeysAsync(PluginHttpContext context)
    {
        if (!TryReadBody<DisableAccountKeysInput>(context.Body, out var input)
            || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("请指定渠道 ID");
        if (input.KeyIds is not { Length: >= 1 and <= 100 }
            || input.KeyIds.Any(string.IsNullOrWhiteSpace))
            return context.BadRequest("请选择 1–100 个要禁用的 Key");
        if (input.KeyRevision is null or < 0)
            return context.BadRequest("缺少有效的 Key 配置版本，请刷新渠道后重试");
        var targetIds = input.KeyIds.ToHashSet(StringComparer.Ordinal);

        var gate = KeyHealthGate(input.Id);
        await gate.WaitAsync(context.CancellationToken);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var existing = await _host.Accounts.GetAsync(input.Id, context.CancellationToken);
                if (existing is null) return context.Json(404, new { error = "渠道不存在" });
                if (!TryReadSettings(existing, out var settings) || existing.Credential is not CustomCredential custom)
                    return context.BadRequest("渠道配置无效");
                if (input.KeyRevision != settings.KeyRevision)
                    return context.Json(409, new { error = "Key 配置已变化，请刷新渠道并核对测试结果后重试" });

                var keys = ReadKeys(settings);
                var existingIds = keys.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
                if (targetIds.Any(id => !existingIds.Contains(id)))
                    return context.BadRequest("存在不属于此渠道的 Key");

                var disabledKeyIds = new List<string>();
                for (var index = 0; index < keys.Count; index++)
                {
                    var key = keys[index];
                    if (!targetIds.Contains(key.Id) || !key.Enabled) continue;
                    keys[index] = key with
                    {
                        Enabled = false,
                        HealthVersion = checked(key.HealthVersion + 1),
                        AutoDisabledAt = null,
                        AutoDisabledStatusCode = null,
                        AutoDisabledSource = null,
                        AutoDisabledFailures = 0
                    };
                    disabledKeyIds.Add(key.Id);
                }

                if (disabledKeyIds.Count == 0)
                    return context.Ok(new { account = ToAccountCard(existing), disabledKeyIds = Array.Empty<string>(), disabledCount = 0 });

                var fields = new Dictionary<string, string?>(custom.Fields, StringComparer.Ordinal);
                var json = JsonNode.Parse(fields["settings"]!)!.AsObject();
                json["keys"] = JsonSerializer.SerializeToNode(keys, JsonOptions);
                json["apiKey"] = "";
                json["keyRevision"] = checked(settings.KeyRevision + 1);
                fields["settings"] = json.ToJsonString(JsonOptions);

                var saved = await _host.Accounts.CompareExchangeCredentialAsync(
                    existing.Id, existing.CredentialVersion, new CustomCredential(fields), context.CancellationToken);
                if (saved is null) continue;

                _keySelector.Remove(saved.Id);
                _keyHealth.Synchronize(saved.Id, keys);
                await RebuildAccountModelSnapshotAsync(context.PluginKey, context.CancellationToken);
                _host.Models.Invalidate(ForwardApiPlatform);
                await TryLogAsync(
                    "account.keys.disabled",
                    $"渠道“{saved.Label ?? saved.Id}”已禁用 {disabledKeyIds.Count} 个 Key",
                    accountId: saved.Id,
                    details: new { keyIds = disabledKeyIds, count = disabledKeyIds.Count });
                return context.Ok(new { account = ToAccountCard(saved), disabledKeyIds = disabledKeyIds.ToArray(), disabledCount = disabledKeyIds.Count });
            }

            return context.Json(409, new { error = "渠道凭据已变化，请刷新后重试" });
        }
        finally { gate.Release(); }
    }

    private sealed class DisableAccountKeysInput
    {
        public string? Id { get; init; }
        public string[]? KeyIds { get; init; }
        public long? KeyRevision { get; init; }
    }

    private List<ChannelKey> AvailableKeys(string channel, ForwardApiSettings settings, string? requestId = null)
        => ReadKeys(settings).Where(k => k.Enabled && !_keyHealth.IsBlocked(channel, k, requestId)).ToList();

    private ChannelKey SelectChannelKey(string channel, ForwardApiSettings settings, string? specified = null,
        bool discovery = false, string? requestId = null, HashSet<string>? excluded = null)
        => _keySelector.Select(channel, AvailableKeys(channel, settings, requestId)
            .Where(k => excluded?.Contains(k.Id) != true).ToList(), settings.KeySelectionMode, specified, discovery);

    private async Task<ForwardApiSettings> RefreshKeySettingsAsync(string channel, ForwardApiSettings fallback,
        CancellationToken cancellationToken)
    {
        var gate = KeyHealthGate(channel);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var account = await _host.Accounts.GetAsync(channel, cancellationToken);
            if (account is null || !TryReadSettings(account, out var settings))
                return new ForwardApiSettings { Keys = [] };
            if (!_keyHealth.HasPending(channel)) return settings;
            foreach (var key in ReadKeys(settings))
                if (_keyHealth.IsBlocked(channel, key)) await PersistKeyDisableAsync(channel, key, cancellationToken);
            var latest = await _host.Accounts.GetAsync(channel, cancellationToken);
            return latest is not null && TryReadSettings(latest, out settings) ? settings : fallback;
        }
        finally { gate.Release(); }
    }

    private async Task ObserveKeyStatusAsync(string channel, ChannelKey selected, KeyAutoDisablePolicy policy,
        string requestId, int status, bool matched, CancellationToken cancellationToken)
    {
        var gate = KeyHealthGate(channel);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var account = await _host.Accounts.GetAsync(channel, cancellationToken);
            if (account is null || !TryReadSettings(account, out var latest) || latest.KeyAutoDisable != policy) return;
            var current = ReadKeys(latest).FirstOrDefault(k => k.Id == selected.Id);
            if (current is null || !current.Enabled || !KeyHealthTracker.SameGeneration(current, selected)) return;
            _keyHealth.Observe(channel, current, requestId, matched, status, policy, "forward");
            await PersistKeyDisableAsync(channel, current, cancellationToken);
        }
        finally { gate.Release(); }
    }

    // 调用者持有渠道锁；CAS 只更新最新凭据中的 Key 字段，不覆盖其他并发编辑。
    private async Task PersistKeyDisableAsync(string channel, ChannelKey selected, CancellationToken cancellationToken)
    {
        var entry = _keyHealth.For(channel, selected);
        ChannelKey? disabled;
        lock (entry.Gate) disabled = entry.PendingDisable;
        if (disabled is null) return;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var account = await _host.Accounts.GetAsync(channel, cancellationToken);
                if (account is null || !TryReadSettings(account, out var settings)) return;
                var keys = ReadKeys(settings);
                var index = keys.FindIndex(k => k.Id == selected.Id);
                if (index < 0 || !KeyHealthTracker.SameGeneration(keys[index], selected))
                { _keyHealth.Synchronize(channel, keys); return; }
                if (!keys[index].Enabled)
                { lock (entry.Gate) { entry.PendingDisable = null; entry.SaveError = null; } return; }
                keys[index] = disabled with { Name = keys[index].Name };
                var fields = new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields);
                var json = JsonNode.Parse(fields["settings"]!)!.AsObject();
                json["keys"] = JsonSerializer.SerializeToNode(keys, JsonOptions);
                json["apiKey"] = "";
                json["keyRevision"] = checked(settings.KeyRevision + 1);
                fields["settings"] = json.ToJsonString(JsonOptions);
                var saved = await _host.Accounts.CompareExchangeCredentialAsync(channel, account.CredentialVersion,
                    new CustomCredential(fields), cancellationToken);
                if (saved is null) continue;
                lock (entry.Gate) { entry.PendingDisable = null; entry.SaveError = null; }
                _keySelector.Remove(channel);
                await RebuildAccountModelSnapshotAsync(ForwardApiPlatform, cancellationToken);
                _host.Models.Invalidate(ForwardApiPlatform);
                await TryLogAsync("key.auto_disabled", "Key 已自动禁用", accountId: channel,
                    statusCode: disabled.AutoDisabledStatusCode,
                    details: new { keyId = selected.Id, disabled.AutoDisabledFailures, disabled.AutoDisabledSource });
                return;
            }
            lock (entry.Gate) entry.SaveError = "禁用状态保存冲突；当前运行已排除，后续操作将重试保存";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            lock (entry.Gate) entry.SaveError = "禁用状态保存失败；当前运行已排除，后续操作将重试保存";
            await TryLogAsync("key.auto_disable.save_failed", "Key 禁用状态保存失败", "Error", accountId: channel,
                details: new { keyId = selected.Id, errorType = error.GetType().Name });
        }
    }
}
