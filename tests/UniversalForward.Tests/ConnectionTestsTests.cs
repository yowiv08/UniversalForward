using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ConnectionTestsTests
{
    private static readonly string[] UnknownModels = ["unknown"];
    private static readonly string[] DuplicateModels = ["a", "a"];
    private static readonly string[] Models = ["a", "b"];
    private static readonly string[] SingleModel = ["a"];
    private static readonly string[] PreferredPaths = ["/v1/messages", "/v1/responses"];

    [TestMethod]
    [DataRow("/v1/chat/completions", """{"choices":[{"message":{"content":"OK"}}]}""", true)]
    [DataRow("/v1/chat/completions", """{"choices":[{}]}""", false)]
    [DataRow("/v1/messages", """{"content":[{"type":"text","text":"OK"}]}""", true)]
    [DataRow("/v1/responses", """{"status":"completed","output":[{"type":"message"}]}""", true)]
    [DataRow("/v1/responses", """{"status":"failed","output":[{"type":"message"}]}""", false)]
    [DataRow("/v1/responses", """{"status":"incomplete","output":[{"type":"message"}]}""", false)]
    [DataRow("/v1/completions", """{"choices":[{"text":"OK"}]}""", true)]
    [DataRow("/v1/messages", """{"error":{"message":"bad"},"content":[{"type":"text"}]}""", false)]
    [DataRow("/v1/chat/completions", "not json", false)]
    public void CompletionShapeMustMatchProtocol(string endpoint, string body, bool expected)
        => Assert.AreEqual(expected, UniversalForwardTerminal.IsTestCompletion(Encoding.UTF8.GetBytes(body), endpoint));

    [TestMethod]
    public async Task StartValidatesModelsAndUsesAccountScopedJob()
    {
        var host = Host();
        var jobs = Mock.Get(host.Services.Jobs);
        JsonElement? received = null;
        jobs.Setup(x => x.StartAsync("connection-test", It.IsAny<JsonElement?>(),
                It.IsAny<PluginJobOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, JsonElement? input, PluginJobOptions options, CancellationToken _) =>
            {
                Assert.AreEqual("account", options.Key);
                Assert.AreEqual("universalforward", options.Platform);
                received = input;
                return Snapshot();
            });
        using var terminal = new UniversalForwardTerminal(host);
        var bad = await terminal.StartConnectionTestsAsync(Context(new { accountId = "account", models = UnknownModels }));
        Assert.AreEqual(400, bad.StatusCode);
        jobs.Verify(x => x.StartAsync(It.IsAny<string>(), It.IsAny<JsonElement?>(),
            It.IsAny<PluginJobOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        var good = await terminal.StartConnectionTestsAsync(Context(new { accountId = "account", models = DuplicateModels }));
        Assert.AreEqual(202, good.StatusCode);
        Assert.AreEqual(1, received!.Value.GetProperty("models").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, received.Value.GetProperty("endpoint").ValueKind);
    }

    [TestMethod]
    public async Task OtherJobCannotBeReadOrCancelled()
    {
        var host = Host();
        var jobs = Mock.Get(host.Services.Jobs);
        jobs.Setup(x => x.Get("job")).Returns(Snapshot() with { Name = "other-job" });
        using var terminal = new UniversalForwardTerminal(host);
        var status = await terminal.ConnectionTestStatusAsync(new PluginHttpContext
        {
            Query = new Dictionary<string, string> { ["id"] = "job" }
        });
        Assert.AreEqual(404, status.StatusCode);
        Assert.AreEqual(404, (await terminal.CancelConnectionTestsAsync(Context(new { id = "job" }))).StatusCode);
        jobs.Verify(x => x.CancelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchProducesProgressAndDoesNotTrustMappedSuccess(bool upstreamError)
    {
        var host = Host(upstreamError);
        var sent = 0;
        using var handler = new ReplyHandler(() =>
        {
            sent++;
            return new HttpResponseMessage(upstreamError ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent("""{"choices":[{"message":{"content":"OK"}}]}""") };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var registration = Registration(terminal);
        var progress = new List<JsonElement>();
        var result = await registration.ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = "account", models = Models, endpoint = "/v1/chat/completions" }),
            value => progress.Add(value!.Value), CancellationToken.None));
        var rows = result!.Value.GetProperty("rows");
        Assert.AreEqual(2, sent);
        Assert.AreEqual(2, rows.GetArrayLength());
        Assert.AreEqual(!upstreamError, rows[0].GetProperty("success").GetBoolean());
        Assert.AreEqual(upstreamError ? 400 : 200, rows[0].GetProperty("originalStatus").GetInt32());
        Assert.AreEqual(200, rows[0].GetProperty("mappedStatus").GetInt32());
        Assert.AreEqual(2, progress[^1].GetProperty("completed").GetInt32());
    }

    [TestMethod]
    [DataRow("data: [DONE]\n\n", false)]
    [DataRow("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]\n\n", true)]
    [DataRow("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n", false)]
    [DataRow("data: {\"error\":{\"message\":\"failed\"}}\n\ndata: [DONE]\n\n", false)]
    public async Task StreamRequiresProtocolEventAndTerminator(string body, bool expected)
    {
        var host = Host();
        using var handler = new ReplyHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = "account", models = SingleModel, endpoint = "/v1/chat/completions", stream = true }),
            _ => { }, CancellationToken.None));
        Assert.AreEqual(expected, result!.Value.GetProperty("rows")[0].GetProperty("success").GetBoolean());
    }

    [TestMethod]
    public async Task CancelledBatchNeverSends()
    {
        var host = Host();
        using var terminal = new UniversalForwardTerminal(host);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Registration(terminal).ExecuteAsync(
            new PluginJobContext("job", "universalforward", "universalforward",
                JsonSerializer.SerializeToElement(new { accountId = "account", models = SingleModel, endpoint = "/v1/chat/completions" }),
                _ => { }, cancelled.Token)));
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    public async Task AutomaticBatchUsesEachModelsPreferredProtocol()
    {
        var host = Host();
        var account = await host.Services.Accounts.GetAsync("account", CancellationToken.None);
        var fields = ((CustomCredential)account!.Credential).Fields;
        var settings = System.Text.Json.Nodes.JsonNode.Parse(fields["settings"]!)!.AsObject();
        settings["modelProtocols"] = System.Text.Json.Nodes.JsonNode.Parse("""
            {"a":{"protocols":["messages"],"preferredProtocol":"messages"},
             "b":{"protocols":["responses"],"preferredProtocol":"responses"}}
            """);
        var updated = new Dictionary<string, string?>(fields) { ["settings"] = settings.ToJsonString() };
        account.Credential = new CustomCredential(updated);
        var paths = new List<string>();
        using var handler = new InspectHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/v1/messages")
            {
                Assert.AreEqual("test-only", request.Headers.GetValues("x-api-key").Single());
                return """{"content":[{"type":"text","text":"OK"}]}""";
            }
            Assert.AreEqual("/v1/responses", path);
            Assert.AreEqual("Bearer test-only", request.Headers.GetValues("Authorization").Single());
            return """{"status":"completed","output":[{"type":"message"}]}""";
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = "account", models = Models }),
            _ => { }, CancellationToken.None));
        CollectionAssert.AreEqual(PreferredPaths, paths);
        Assert.IsTrue(result!.Value.GetProperty("rows").EnumerateArray().All(x => x.GetProperty("success").GetBoolean()));
        Assert.AreEqual(400, (await terminal.StartConnectionTestsAsync(Context(new
        { accountId = "account", models = SingleModel, endpoint = "/v1/responses" }))).StatusCode);
    }

    private sealed class InspectHandler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request)) });
    }

    private static PluginJobRegistration Registration(UniversalForwardTerminal terminal)
    {
        PluginJobRegistration? registration = null;
        var builder = new Mock<IPluginBuilder>();
        builder.Setup(x => x.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(r => registration = r);
        terminal.Configure(builder.Object);
        return registration!;
    }

    private static IPluginHost Host(bool mapping = false)
    {
        var host = PluginTestHost.Create("universalforward");
        Mock.Get(host.Services).SetupGet(x => x.Jobs).Returns(Mock.Of<IPluginJobs>());
        var account = new Account
        {
            Id = "account", PluginKey = "universalforward", Platform = "universalforward",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = JsonSerializer.Serialize(new
                {
                    baseUrl = "https://upstream.example/v1", apiKey = "test-only",
                    requestPolicy = new ForwardRequestPolicy { StatusCodeMapping = mapping ? new() { [400] = 200 } : [] }
                }),
                ["models"] = """["a","b"]""", ["modelsConfigured"] = "true"
            })
        };
        Mock.Get(host.Services.Accounts).Setup(x => x.GetAsync("account", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        return host;
    }

    private static PluginHttpContext Context(object input) => new()
    {
        PluginKey = "universalforward", Platform = "universalforward", Body = JsonSerializer.SerializeToElement(input)
    };

    private static PluginJobSnapshot Snapshot() => new("job", "connection-test", "universalforward", "account",
        PluginJobState.Queued, DateTimeOffset.UtcNow, null, null, null, null, null);

    private sealed class ReplyHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply());
        }
    }
}
