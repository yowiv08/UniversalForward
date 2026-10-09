using System.Net;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class PageAndDiscoveryTests
{
    [TestMethod]
    public void MainPageEmbedsAllAssets()
    {
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var html = terminal.GetMainPage().Html;
        StringAssert.Contains(html, "tab-models");
        StringAssert.Contains(html, "tab-reasoning");
        StringAssert.Contains(html, "function readReasoningPolicy()");
        StringAssert.Contains(html, "templateCodex");
        StringAssert.Contains(html, "templateClaude");
        StringAssert.Contains(html, "mappingVisualPane");
        Assert.IsFalse(html.Contains("/*__STYLE__*/", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("/*__SCRIPT__*/", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("/*__REASONING__*/", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("headerPass", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("confirm(", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DiscoveryUsesDraftConnectionAndHeadersWithoutSaving(bool existing)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "b", Secret = "stored-secret" }]);
        var host = ChannelKeysTests.Host(account);
        var previous = account.Credential;
        var sends = 0;
        using var handler = new Handler(request =>
        {
            sends++;
            Assert.AreEqual("https://draft.example/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(existing ? "Bearer stored-secret" : "Bearer draft-secret", request.Headers.Authorization!.ToString());
            Assert.AreEqual("codex_exec", request.Headers.GetValues("Originator").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"id":"discovered-model"}]}""") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        {
            id = existing ? account.Id : null,
            keyId = existing ? "b" : null,
            apiKey = existing ? null : "draft-secret",
            baseUrl = "https://draft.example/v1",
            headerOverride = new Dictionary<string, string>
            { ["Authorization"] = "Bearer {api_key}", ["Originator"] = "codex_exec" }
        }));
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(1, sends);
        Assert.AreSame(previous, account.Credential);
        Mock.Get(host.Services.Accounts).Verify(x => x.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()), Times.Never);
        Mock.Get(host.Services.Accounts).Verify(x => x.CompareExchangeCredentialAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task InvalidDraftHeadersDoNotSendRequests()
    {
        var host = PluginTestHost.Create("universalforward");
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        {
            baseUrl = "https://draft.example", apiKey = "draft-secret",
            headerOverride = new Dictionary<string, string> { ["Host"] = "other.example" }
        }));
        Assert.AreEqual(400, result.StatusCode);
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
