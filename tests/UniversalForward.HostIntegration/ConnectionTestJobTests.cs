using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;
using Router.Infrastructure.Services;

namespace UniversalForward.HostIntegration;

[TestClass]
public sealed class ConnectionTestJobTests
{
    private static readonly string[] Models = ["model"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealHostJobQuotaAllows100LargeResponses(bool stream)
    {
        await using var jobs = new PluginJobManager("universalforward", Mock.Of<IPluginLogSink>());
        var account = new Account
        {
            Id = "channel", Platform = "universalforward", PluginKey = "universalforward",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = JsonSerializer.Serialize(new { baseUrl = "https://upstream.example", keys = Enumerable.Range(0, 100)
                    .Select(i => new { id = "key-" + i, name = "Key " + i, secret = "test-" + i, enabled = true }) }),
                ["models"] = "[\"model\"]", ["modelsConfigured"] = "true"
            })
        };
        var services = new Mock<IPluginServices>();
        var accounts = new Mock<IPluginAccounts>();
        accounts.Setup(a => a.GetAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        services.SetupGet(s => s.Accounts).Returns(accounts.Object);
        services.SetupGet(s => s.Jobs).Returns(jobs);
        services.SetupGet(s => s.Log).Returns(Mock.Of<IPluginLogSink>());
        var http = new Mock<IPluginHttpServices>();
        var text = new string('中', 20000) + "\n\"转义😀\"";
        var json = JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } });
        var content = stream ? "data: {\"type\":\"response.created\"}\n\ndata: {\"type\":\"response.completed\",\"response\":" + json + "}\n\n" : json;
        using var handler = new Handler(content, stream);
        http.Setup(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => new HttpClient(handler, false));
        services.SetupGet(s => s.Http).Returns(http.Object);
        var host = new Mock<IPluginHost>(); host.SetupGet(h => h.Services).Returns(services.Object);
        using var terminal = new UniversalForwardTerminal(host.Object);
        var builder = new Mock<IPluginBuilder>();
        builder.Setup(b => b.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(r => jobs.Register([r]));
        terminal.Configure(builder.Object);
        var started = await terminal.StartConnectionTestsAsync(new PluginHttpContext
        {
            PluginKey = "universalforward", Platform = "universalforward",
            Body = JsonSerializer.SerializeToElement(new { accountId = account.Id, models = Models, keyMode = "all", stream })
        });
        Assert.AreEqual(202, started.StatusCode);
        var id = ((PluginJobSnapshot)started.Body!).Id;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completed = await jobs.WaitAsync(id, deadline.Token);
        Assert.AreEqual(PluginJobState.Completed, completed!.State, completed.Error);
        Assert.AreEqual(100, completed.Result!.Value.GetProperty("completed").GetInt32());
        Assert.IsLessThan(64 * 1024, Encoding.UTF8.GetByteCount(completed.Progress!.Value.GetRawText()));
        Assert.IsLessThan(256 * 1024, Encoding.UTF8.GetByteCount(completed.Result.Value.GetRawText()));
        Assert.IsFalse(completed.Result.Value.TryGetProperty("rows", out _));
        for (var offset = 0; offset < 100; offset += 20)
        {
            var page = JsonSerializer.SerializeToElement(terminal.TestResults.Page(id, offset, 20));
            Assert.IsTrue(page.GetProperty("rows").EnumerateArray().All(r => r.GetProperty("success").GetBoolean()));
        }
        var detail = JsonSerializer.SerializeToElement(terminal.TestResults.Detail(id, 0, 0));
        Assert.IsFalse(detail.GetProperty("done").GetBoolean());
        Assert.IsFalse(string.IsNullOrEmpty(detail.GetProperty("text").GetString()));
    }

    private sealed class Handler(string body, bool stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json") });
    }
}
