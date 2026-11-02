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
        Assert.IsFalse(html.Contains("/*__APPEARANCE__*/", StringComparison.Ordinal));
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

    [TestMethod]
    [DataRow(null, null, "2023-06-01")]
    [DataRow("2024-10-22", null, "2024-10-22")]
    [DataRow(null, "2025-01-01", "2025-01-01")]
    public async Task DiscoverySendsAnthropicVersionUnlessConfigured(string? headerOverrideVersion, string? replaceHeaderVersion, string expected)
    {
        var host = PluginTestHost.Create("universalforward");
        var sends = 0;
        using var handler = new Handler(request =>
        {
            sends++;
            Assert.AreEqual("Bearer draft-secret", request.Headers.Authorization!.ToString());
            Assert.AreEqual(expected, request.Headers.GetValues("anthropic-version").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"type":"model","id":"claude-opus-5-5"}]}""") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var headerOverride = new Dictionary<string, string>();
        if (headerOverrideVersion is not null) headerOverride["anthropic-version"] = headerOverrideVersion;
        var extraParams = new Dictionary<string, object>();
        if (replaceHeaderVersion is not null)
            extraParams["ReplaceHeaders"] = new Dictionary<string, string> { ["Anthropic-Version"] = replaceHeaderVersion };
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        {
            baseUrl = "https://api.anthropic.com", apiKey = "draft-secret", headerOverride, extraParams
        }));
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(1, sends);
        StringAssert.Contains(System.Text.Json.JsonSerializer.Serialize(result), "claude-opus-5-5");
    }

    [TestMethod]
    [DataRow("https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/models")]
    [DataRow("https://api.anthropic.com/v1/messages/", "https://api.anthropic.com/v1/models")]
    [DataRow("https://upstream.example/V1/Responses", "https://upstream.example/V1/models")]
    [DataRow("https://upstream.example/gateway/v1/models", "https://upstream.example/gateway/v1/models")]
    [DataRow("https://upstream.example/gateway/v1", "https://upstream.example/gateway/v1/models")]
    [DataRow("https://upstream.example/gateway", "https://upstream.example/gateway/v1/models")]
    public async Task DiscoveryIgnoresEndpointPathAtEndOfBaseUrl(string baseUrl, string expectedUrl)
    {
        var host = PluginTestHost.Create("universalforward");
        var requested = new List<string>();
        using var handler = new Handler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"id":"discovered-model"}]}""") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { baseUrl, apiKey = "draft-secret" }));
        Assert.AreEqual(200, result.StatusCode);
        CollectionAssert.AreEqual(new[] { expectedUrl }, requested);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
