using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ForwardRetryTests
{
    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(2, 3)]
    [DataRow(10, 11)]
    public async Task RetriesSameRequestWithoutPenalty(int retries, int sends)
    {
        var bodies = new List<string>();
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                Assert.AreEqual("upstream.example", request.RequestUri!.Host);
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
            });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, new() { MaxRetries = retries }));
        Assert.AreEqual(sends, bodies.Count);
        Assert.AreEqual(1, bodies.Distinct().Count());
        Assert.AreEqual("vendor/model", JsonDocument.Parse(bodies[0]).RootElement.GetProperty("model").GetString());
        Assert.AreEqual(429, result.Response.StatusCode);
        Assert.AreEqual(PluginAttemptOutcome.NoPenalty, result.Attempt.Outcome);
    }

    [TestMethod]
    public async Task MappingHappensAfterRetryDecision()
    {
        var client = Client(HttpStatusCode.BadRequest);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, new()
        {
            MaxRetries = 10, StatusCodeMapping = new() { [400] = 500 }
        }));
        Assert.AreEqual(500, result.Response.StatusCode);
        Assert.AreEqual(400, result.Attempt.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
        Assert.AreEqual("body", Encoding.UTF8.GetString(result.Response.RawContent!));
    }

    [TestMethod]
    public async Task TransportFailureUsesOneSharedBudget()
    {
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, new() { MaxRetries = 2 }));
        Assert.AreEqual(3, client.Invocations.Count);
        Assert.AreEqual(502, result.Response.StatusCode);
        Assert.AreEqual(PluginAttemptOutcome.NoPenalty, result.Attempt.Outcome);
    }

    [TestMethod]
    public async Task CancellationDoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage _, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(ct);
            });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => terminal.InvokeAsync(Context(client.Object, new() { MaxRetries = 10 }, ct: cts.Token)));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task UnconsumedStreamCanBeReleased()
    {
        var content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream");
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, new(), stream: true));
        Assert.IsNotNull(result.Response.Lifetime);
        await result.Response.Lifetime.DisposeAsync();
        await result.Response.Lifetime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsByteArrayAsync());
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(11)]
    public async Task BadPolicyDoesNotSend(int retries)
    {
        var client = Client(HttpStatusCode.OK);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        Assert.AreEqual(400, (await terminal.InvokeAsync(Context(client.Object, new() { MaxRetries = retries }))).Response.StatusCode);
        Assert.AreEqual(0, client.Invocations.Count);
    }

    [TestMethod]
    public async Task HeaderTimeoutRetriesButTotalTimeoutStops()
    {
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage _, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, new()
        {
            HeaderTimeoutSeconds = 1, TotalTimeoutSeconds = 3, MaxRetries = 10
        }));
        Assert.AreEqual(504, result.Response.StatusCode);
        Assert.IsTrue(client.Invocations.Count >= 2 && client.Invocations.Count <= 3);
        using var errorBody = JsonDocument.Parse(result.Response.RawContent!);
        StringAssert.Contains(errorBody.RootElement.GetProperty("error").GetString()!, "总时限");
    }

    [TestMethod]
    public async Task StreamIdleTimeoutClosesResponseWithoutRetry()
    {
        using var stream = new WaitingStream();
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("text/event-stream");
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(Context(client.Object, new()
        {
            StreamIdleTimeoutSeconds = 1, MaxRetries = 10
        }, stream: true));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in result.Response.RawStream!) { }
        });
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    private sealed class WaitingStream : System.IO.Stream
    {
        private bool _started;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                _started = true;
                var prefix = Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\n");
                prefix.CopyTo(buffer);
                return prefix.Length;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static Mock<IPluginHttpClient> Client(HttpStatusCode code)
    {
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("body") }));
        return client;
    }

    private static PluginAttemptContext Context(IPluginHttpClient client, ForwardRequestPolicy policy,
        bool stream = false, CancellationToken ct = default) => new()
    {
        PluginKey = "universalforward", PlatformName = "universalforward", HttpClient = client,
        CancellationToken = ct,
        Account = new Account
        {
            Id = "same-account", PluginKey = "universalforward", Platform = "universalforward",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = JsonSerializer.Serialize(new
                {
                    baseUrl = "https://upstream.example/v1", apiKey = "test-only", requestPolicy = policy
                }),
                ["modelsConfigured"] = "true", ["models"] = """["vendor/model"]"""
            })
        },
        Request = new AdapterRequest
        {
            Model = "universalforward/vendor/model", Endpoint = "/v1/responses", Stream = stream,
            OriginalBody = JsonSerializer.SerializeToElement(new { model = "universalforward/vendor/model", messages = new[] { new { role = "user", content = "test" } } })
        }
    };
}
