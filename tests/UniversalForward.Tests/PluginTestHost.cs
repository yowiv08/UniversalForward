using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

internal static class PluginTestHost
{
    public static IPluginHost Create(string key, IProxyPoolHttpClientFactory? pool = null)
    {
        var capabilities = new Mock<IPluginServices>();
        var http = new Mock<IPluginHttpServices>();
        http.SetupGet(value => value.Pool).Returns(pool ?? Mock.Of<IProxyPoolHttpClientFactory>());
        capabilities.SetupGet(value => value.PluginKey).Returns(key);
        capabilities.SetupGet(value => value.Http).Returns(http.Object);
        capabilities.SetupGet(value => value.State).Returns(new PluginStateServices(Mock.Of<IPluginStateStore>(), Mock.Of<IPluginStateStore>()));
        capabilities.SetupGet(value => value.Accounts).Returns(Mock.Of<IPluginAccounts>());
        capabilities.SetupGet(value => value.Models).Returns(Mock.Of<IPluginModels>());
        capabilities.SetupGet(value => value.Tasks).Returns(Mock.Of<IPluginTasks>());
        capabilities.SetupGet(value => value.Log).Returns(Mock.Of<IPluginLogSink>());
        capabilities.SetupGet(value => value.Execution).Returns(new PluginExecutionOptions());
        var host = new Mock<IPluginHost>(MockBehavior.Strict);
        host.SetupGet(value => value.PluginKey).Returns(key);
        host.SetupGet(value => value.Services).Returns(capabilities.Object);
        host.Setup(value => value.GetServiceAsync<IProxyStore>()).ThrowsAsync(new NotSupportedException());
        host.Setup(value => value.GetServiceAsync<IProxySubscriptionService>()).ThrowsAsync(new NotSupportedException());
        host.Setup(value => value.GetServiceAsync<IProxyHttpClientFactory>()).ThrowsAsync(new NotSupportedException());
        return host.Object;
    }

    public static Mock<IProxyHttpClientFactory> ConfigureProxyServices(IPluginHost host, Func<ProxyEndpoint, HttpClient>? create = null,
        IReadOnlyList<ProxyEndpoint>? proxies = null, IReadOnlyList<ProxySubscription>? subscriptions = null)
    {
        proxies ??= [new ProxyEndpoint { Id = "node", SubscriptionId = "manual", Host = "127.0.0.1", Port = 1080 }];
        subscriptions ??= [];
        var store = new Mock<IProxyStore>();
        store.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(proxies);
        var sources = new Mock<IProxySubscriptionService>();
        sources.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subscriptions);
        var clients = new Mock<IProxyHttpClientFactory>();
        if (create is not null)
            clients.Setup(value => value.CreateClient(It.IsAny<ProxyEndpoint>(), "universalforward"))
                .Returns((ProxyEndpoint proxy, string _) => create(proxy));
        Mock.Get(host).Setup(value => value.GetServiceAsync<IProxyStore>()).ReturnsAsync(store.Object);
        Mock.Get(host).Setup(value => value.GetServiceAsync<IProxySubscriptionService>()).ReturnsAsync(sources.Object);
        Mock.Get(host).Setup(value => value.GetServiceAsync<IProxyHttpClientFactory>()).ReturnsAsync(clients.Object);
        return clients;
    }

    public static Mock<IProxyHttpClientFactory> ProxyClients(IPluginHost host)
        => Mock.Get(host.GetServiceAsync<IProxyHttpClientFactory>().GetAwaiter().GetResult());
}
