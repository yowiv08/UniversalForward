using Router.Contracts.Host;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    private const string ProxyUnavailableMessage = "代理池无可用节点，未回退直连";

    private static void ValidateNetworkMode(string mode)
    {
        if (mode is not ("direct" or "proxyPool"))
            throw new FormatException("networkMode 必须为 direct 或 proxyPool");
    }

    private Task<HttpClient> CreateNetworkClientAsync(string mode, bool redirects, CancellationToken cancellationToken)
    {
        ValidateNetworkMode(mode);
        return mode == "proxyPool"
            ? _host.Http.Pool.CreateClientAsync(new ProxyPoolHttpClientOptions
            {
                AllowDirectFallback = false, AllowAutoRedirect = redirects,
                RequestTimeout = Timeout.InfiniteTimeSpan
            }, cancellationToken)
            : Task.FromResult(_host.Http.CreateDirectClient(new PluginHttpClientOptions
            {
                AllowAutoRedirect = redirects, RequestTimeout = Timeout.InfiniteTimeSpan
            }));
    }
}
