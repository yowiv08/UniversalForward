using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ForwardResponseHandlingTests
{
    private const string Responses = "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}\n\n";
    private const string Messages = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"usage\":{\"input_tokens\":3}}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\ndata: {\"type\":\"message_stop\"}\n\n";

    [TestMethod]
    [DataRow("codex", false)]
    [DataRow("codex", true)]
    [DataRow("claude", false)]
    [DataRow("claude", true)]
    public async Task TemplatesUseUpstreamStreamingButHonorDownstreamMode(string profile, bool stream)
    {
        using var content = new TrackedContent(profile == "codex" ? Responses : Messages, "text/event-stream");
        var client = Client(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.IsTrue(body["stream"]!.GetValue<bool>());
            Assert.AreEqual("question", body[profile == "codex" ? "instructions" : "system"] is JsonArray
                ? body["system"]![1]!["text"]!.ToString() : body["instructions"]!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, profile, stream));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(stream, result.Response.IsStreaming);
        if (stream)
        {
            Assert.IsNotNull(result.Response.RawStream);
            using var collected = new MemoryStream();
            await foreach (var chunk in result.Response.RawStream) await collected.WriteAsync(chunk);
            Assert.AreEqual(profile == "codex" ? Responses : Messages, Encoding.UTF8.GetString(collected.ToArray()));
        }
        else
        {
            Assert.IsNull(result.Response.RawStream);
            Assert.AreEqual("application/json", result.Response.ContentType);
            var body = JsonNode.Parse(result.Response.RawContent!)!;
            Assert.AreEqual(profile == "codex" ? "resp" : "msg", body["id"]!.ToString());
            Assert.AreEqual(3, body["usage"]!["input_tokens"]!.GetValue<int>());
            Assert.AreEqual(2, body["usage"]!["output_tokens"]!.GetValue<int>());
        }
        Assert.IsTrue(content.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(520, "")]
    [DataRow(503, "")]
    [DataRow(500, "{\"error\":{\"message\":\"overloaded\",\"code\":\"get_channel_failed\"}}")]
    public async Task KeepsUpstreamFailuresAndExplainsEmptyErrors(int status, string text)
    {
        var client = Client(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text) };
            response.Headers.Add("x-request-id", "upstream-id");
            response.Headers.Add("cf-ray", "ray-id");
            return Task.FromResult(response);
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, "codex", false));
        Assert.AreEqual(status, result.Response.StatusCode);
        Assert.AreEqual(status, result.Attempt.StatusCode);
        Assert.IsFalse(result.Response.IsStreaming);
        var body = JsonNode.Parse(result.Response.RawContent!)!;
        if (text.Length == 0)
        {
            Assert.AreEqual("empty_upstream_response", body["error"]!["code"]!.ToString());
            Assert.AreEqual(status, body["error"]!["upstream_status"]!.GetValue<int>());
            Assert.AreEqual("upstream-id", body["error"]!["upstream_request_id"]!.ToString());
            Assert.AreEqual("trace", body["error"]!["trace_id"]!.ToString());
            Assert.IsFalse(body.ToJsonString().Contains("channel-secret", StringComparison.Ordinal));
        }
        else Assert.AreEqual(text, Encoding.UTF8.GetString(result.Response.RawContent!));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task Retry520UsesConfiguredBudgetAndSameBody()
    {
        var bodies = new List<string>();
        var client = Client(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("Bearer channel-secret", request.Headers.GetValues("Authorization").Single());
            return new HttpResponseMessage((HttpStatusCode)(bodies.Count == 1 ? 520 : 200))
            {
                Content = new StringContent(bodies.Count == 1 ? "" : Responses, Encoding.UTF8, "text/event-stream")
            };
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, "codex", false, 1));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(2, bodies.Count);
        Assert.AreEqual(bodies[0], bodies[1]);
    }

    [TestMethod]
    [DataRow("data: {\"type\":\"response.created\"}\n\n", 502)]
    [DataRow("data: {\"type\":\"error\",\"error\":{\"message\":\"overloaded\"}}\n\n", 502)]
    [DataRow("", 502)]
    public async Task AggregationRejectsBrokenStreamsWithoutReplaying(string frames, int expected)
    {
        using var content = new TrackedContent(frames, "text/event-stream");
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, "codex", false, 2));
        Assert.AreEqual(expected, result.Response.StatusCode);
        Assert.IsTrue(content.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
        Assert.AreEqual("invalid_upstream_response", JsonNode.Parse(result.Response.RawContent!)!["error"]!["code"]!.ToString());
    }

    [TestMethod]
    public async Task NonstreamReadTimeoutIsBoundedAndDoesNotRetry()
    {
        using var stream = new WaitingStream();
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("text/event-stream");
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var context = Context(client.Object, "codex", false, 3);
        var custom = (CustomCredential)context.Account.Credential;
        var fields = new Dictionary<string, string?>(custom.Fields);
        var settings = JsonNode.Parse(fields["settings"]!)!;
        settings["requestPolicy"]!["totalTimeoutSeconds"] = 1;
        fields["settings"] = settings.ToJsonString();
        context.Account.Credential = new CustomCredential(fields);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(504, result.Response.StatusCode);
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    private sealed class WaitingStream : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static Mock<IPluginHttpClient> Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) => reply(request));
        return client;
    }

    private static PluginAttemptContext Context(IPluginHttpClient client, string profile, bool stream, int retries = 0) => new()
    {
        PluginKey = "universalforward", PlatformName = "universalforward", HttpClient = client,
        CancellationToken = CancellationToken.None, TraceId = "trace",
        Account = new Account
        {
            Id = "channel", PluginKey = "universalforward", Platform = "universalforward",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = JsonSerializer.Serialize(new
                {
                    baseUrl = "https://upstream.example", apiKey = "channel-secret",
                    headerOverride = profile == "codex" ? new Dictionary<string, string> { ["Originator"] = "codex_exec" }
                        : new Dictionary<string, string> { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" },
                    requestPolicy = new ForwardRequestPolicy { MaxRetries = retries }
                }),
                ["models"] = """["model"]""", ["modelsConfigured"] = "true"
            })
        },
        Request = new AdapterRequest
        {
            Model = "universalforward/model", Endpoint = profile == "codex" ? "/v1/responses" : "/v1/messages",
            Stream = stream, OriginalBody = JsonSerializer.SerializeToElement(new
            {
                model = "universalforward/model", stream, input = "question", instructions = "question",
                system = "question", messages = new[] { new { role = "user", content = "question" } }
            })
        }
    };

    private sealed class TrackedContent(string text, string type) : StringContent(text, Encoding.UTF8, type)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
