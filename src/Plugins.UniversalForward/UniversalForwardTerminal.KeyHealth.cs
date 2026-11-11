using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    private readonly KeyHealthTracker _keyHealth = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyHealthGates = new(StringComparer.Ordinal);
    private SemaphoreSlim KeyHealthGate(string channel) => _keyHealthGates.GetOrAdd(channel, _ => new(1, 1));

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
        string requestId, int status, bool matched, bool isTest, CancellationToken cancellationToken)
    {
        var gate = KeyHealthGate(channel);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var account = await _host.Accounts.GetAsync(channel, cancellationToken);
            if (account is null || !TryReadSettings(account, out var latest) || latest.KeyAutoDisable != policy) return;
            var current = ReadKeys(latest).FirstOrDefault(k => k.Id == selected.Id);
            if (current is null || !current.Enabled || !KeyHealthTracker.SameGeneration(current, selected)) return;
            _keyHealth.Observe(channel, current, requestId, matched, status, policy, isTest ? "connection-test" : "forward");
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
