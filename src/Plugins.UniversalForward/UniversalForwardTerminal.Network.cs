using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

internal sealed record ProxyPoolSubscription(string Id, string Name, bool Enabled);
internal sealed record ProxyPoolNode(string Id, string SubscriptionId, string Address, string State, bool Available);
internal sealed record ProxyPoolCatalog(IReadOnlyList<ProxyPoolSubscription> Subscriptions, IReadOnlyList<ProxyPoolNode> Nodes);

public sealed partial class UniversalForwardTerminal
{
    private const string ProxyUnavailableMessage = "指定代理范围内无可用节点，未切换其他节点或回退直连";
    private const string ProxyServicesUnavailableMessage = "当前宿主未提供公开代理目录服务，无法列出或固定代理节点";
    private readonly Lazy<Task<ProxySelectionServices>> _proxyServices = new(() => LoadProxyServicesAsync(host));

    private sealed record ProxySelectionServices(IProxyStore Store, IProxySubscriptionService Subscriptions, IProxyHttpClientFactory Clients);
    private sealed class ProxyServicesUnavailableException(Exception inner) : NotSupportedException(ProxyServicesUnavailableMessage, inner);

    private static async Task<ProxySelectionServices> LoadProxyServicesAsync(IPluginHost serviceHost)
    {
        try
        {
            // Contracts 2.0 的公开服务兼容入口；不依赖宿主或 Infrastructure 程序集。
            var store = serviceHost.GetServiceAsync<IProxyStore>();
            var subscriptions = serviceHost.GetServiceAsync<IProxySubscriptionService>();
            var clients = serviceHost.GetServiceAsync<IProxyHttpClientFactory>();
            await Task.WhenAll(store, subscriptions, clients);
            return new ProxySelectionServices(await store ?? throw new NotSupportedException(),
                await subscriptions ?? throw new NotSupportedException(), await clients ?? throw new NotSupportedException());
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        {
            throw new ProxyServicesUnavailableException(error);
        }
    }

    private static void ValidateNetworkMode(string mode)
    {
        if (mode is not ("direct" or "proxyPool"))
            throw new FormatException("networkMode 必须为 direct 或 proxyPool");
    }

    private static string[] NormalizeProxySubscriptionIds(string[]? ids)
    {
        if (ids is null) return [];
        if (ids.Length > 100) throw new FormatException("代理来源最多 100 个");
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Trim().Length > 256 || id.Any(char.IsControl)))
            throw new FormatException("代理来源 ID 不能为空、超过 256 字符或包含控制字符");
        return ids.Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? NormalizeProxyId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Trim().Length > 256 || id.Any(char.IsControl))
            throw new FormatException("代理节点 ID 不能超过 256 字符或包含控制字符");
        return id.Trim();
    }

    [PluginEndpoint("GET", "network/proxies")]
    public async Task<PluginResult> ListNetworkProxiesAsync(PluginHttpContext context)
    {
        try
        {
            var services = await _proxyServices.Value.WaitAsync(context.CancellationToken);
            var subscriptions = await services.Subscriptions.ListAsync(context.CancellationToken);
            var nodes = await services.Store.ListAsync(context.CancellationToken);
            var enabled = EnabledProxySources(subscriptions, []);
            var now = DateTimeOffset.UtcNow;
            var catalog = new ProxyPoolCatalog(subscriptions
                .Where(subscription => !subscription.Id.Equals("manual", StringComparison.OrdinalIgnoreCase))
                .Select(subscription => new ProxyPoolSubscription(subscription.Id, subscription.Name, subscription.Enabled))
                .Append(new ProxyPoolSubscription("manual", "手动节点", true)).ToArray(),
                nodes.Select(proxy => ProxySnapshot(proxy, IsProxyAvailable(proxy, enabled, now))).ToArray());
            return context.Ok(new
            {
                subscriptions = catalog.Subscriptions,
                nodes = catalog.Nodes,
                execution = ExecutionLimits()
            });
        }
        catch (NotSupportedException)
        {
            return context.Json(503, new
            {
                error = ProxyServicesUnavailableMessage,
                code = "proxy_selection_not_supported"
            });
        }
    }

    private async Task<HttpClient> CreateNetworkClientAsync(ForwardApiSettings settings,
        CancellationToken cancellationToken, Action<ProxyPoolNode?>? selected = null)
    {
        ValidateNetworkMode(settings.NetworkMode);
        if (settings.NetworkMode == "proxyPool")
        {
            var services = await _proxyServices.Value.WaitAsync(cancellationToken);
            var subscriptions = await services.Subscriptions.ListAsync(cancellationToken);
            var enabled = EnabledProxySources(subscriptions, settings.ProxySubscriptionIds);
            var nodes = await services.Store.ListAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var candidates = nodes.Where(proxy => IsProxyAvailable(proxy, enabled, now)
                && (settings.ProxyId is null || proxy.Id.Equals(settings.ProxyId, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Length == 0) throw new ProxyPoolUnavailableException();
            var proxy = candidates[Random.Shared.Next(candidates.Length)];
            var client = services.Clients.CreateClient(proxy, ForwardApiPlatform);
            try
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                selected?.Invoke(ProxySnapshot(proxy, true));
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        selected?.Invoke(null);
        return _host.Http.CreateDirectClient(new PluginHttpClientOptions
        {
            AllowAutoRedirect = false, RequestTimeout = Timeout.InfiniteTimeSpan
        });
    }

    private static HashSet<string> EnabledProxySources(IEnumerable<ProxySubscription> subscriptions, string[] requested)
    {
        var allowed = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = subscriptions.Where(subscription => subscription.Enabled
            && (allowed.Count == 0 || allowed.Contains(subscription.Id)))
            .Select(subscription => subscription.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0 || allowed.Contains("manual")) enabled.Add("manual");
        return enabled;
    }

    private static bool IsProxyAvailable(ProxyEndpoint proxy, HashSet<string> enabled, DateTimeOffset now)
        => enabled.Contains(proxy.SubscriptionId) && ResourceAvailability.IsAvailable(proxy, now)
            && !string.IsNullOrWhiteSpace(proxy.Host) && proxy.Port is > 0 and <= 65535;

    private static ProxyPoolNode ProxySnapshot(ProxyEndpoint proxy, bool available)
    {
        var hostName = proxy.Host.Contains(':') && !proxy.Host.StartsWith('[') ? $"[{proxy.Host}]" : proxy.Host;
        var scheme = proxy.Scheme switch { ProxyScheme.Https => "https", ProxyScheme.Socks5 => "socks5", _ => "http" };
        return new ProxyPoolNode(proxy.Id, proxy.SubscriptionId, $"{scheme}://{hostName}:{proxy.Port}", proxy.Status.State.ToString(), available);
    }
}
