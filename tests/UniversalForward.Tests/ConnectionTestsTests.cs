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

    [TestMethod]
    [DataRow("codex", "/v1/responses")]
    [DataRow("claude", "/v1/messages")]
    public async Task ClientProfileTestsSendMatchingBodiesAndPinIdentityAcrossRetries(string profile, string endpoint)
    {
        var host = Host();
        var account = await host.Services.Accounts.GetAsync("account", CancellationToken.None);
        var fields = ((CustomCredential)account!.Credential).Fields;
        var settings = System.Text.Json.Nodes.JsonNode.Parse(fields["settings"]!)!.AsObject();
        settings["endpoints"] = new System.Text.Json.Nodes.JsonArray(endpoint);
        settings["headerOverride"] = System.Text.Json.Nodes.JsonNode.Parse(profile == "codex"
            ? """{"Originator":"codex_exec","User-Agent":"Codex Desktop/0.146.0-alpha.9.2 (Windows 10.0.26200; x86_64) unknown (Codex Desktop; 26.727.51351)"}"""
            : """{"x-app":"cli","anthropic-beta":"claude-code-20250219","User-Agent":"claude-cli/2.1.161 (external, cli)"}""");
        settings["requestPolicy"] = System.Text.Json.Nodes.JsonNode.Parse("""{"maxRetries":1}""");
        account.Credential = new CustomCredential(new Dictionary<string, string?>(fields)
            { ["settings"] = settings.ToJsonString() });
        var bodies = new List<string>();
        var sessions = new List<string>();
        using var handler = new ProfileHandler(async request =>
        {
            var text = await request.Content!.ReadAsStringAsync();
            bodies.Add(text);
            var body = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            var session = request.Headers.GetValues(profile == "codex" ? "Session-Id" : "x-claude-code-session-id").Single();
            sessions.Add(session);
            Assert.IsTrue(body["stream"]!.GetValue<bool>());
            Assert.AreEqual("text/event-stream", request.Headers.Accept.Single().MediaType);
            if (profile == "codex")
            {
                Assert.AreEqual("Bearer test-only", request.Headers.GetValues("Authorization").Single());
                Assert.AreEqual("Codex Desktop/0.146.0-alpha.9.2 (Windows 10.0.26200; x86_64) unknown (Codex Desktop; 26.727.51351)",
                    string.Join(" ", request.Headers.GetValues("User-Agent")));
                Assert.AreEqual(session, body["client_metadata"]!["session_id"]!.ToString());
                Assert.AreEqual(request.Headers.GetValues("X-Codex-Turn-Metadata").Single(),
                    body["client_metadata"]!["x-codex-turn-metadata"]!.ToString());
                Assert.IsNull(body["max_output_tokens"]);
            }
            else
            {
                Assert.AreEqual("test-only", request.Headers.GetValues("x-api-key").Single());
                Assert.AreEqual("claude-cli/2.1.161 (external, cli)", string.Join(" ", request.Headers.GetValues("User-Agent")));
                Assert.AreEqual("?beta=true", request.RequestUri!.Query);
                Assert.AreEqual(session, System.Text.Json.Nodes.JsonNode.Parse(body["metadata"]!["user_id"]!.ToString())!["session_id"]!.ToString());
            }
            if (bodies.Count == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("retry") };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(profile == "codex"
                    ? "data: {\"type\":\"response.created\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\"}]}}\n\n"
                    : "data: {\"type\":\"message_start\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        });
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await Registration(terminal).ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = "account", models = SingleModel, endpoint, stream = false }),
            _ => { }, CancellationToken.None));
        Assert.IsTrue(result!.Value.GetProperty("rows")[0].GetProperty("success").GetBoolean(), result.ToString());
        Assert.AreEqual(2, bodies.Count);
        Assert.AreEqual(bodies[0], bodies[1]);
        Assert.AreEqual(sessions[0], sessions[1]);
    }

    private sealed class ProfileHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => reply(request);
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
