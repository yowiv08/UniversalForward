using System.Net;
using System.Text;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class StreamingRetryTests
{
    private const string Text = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"你好\"}\n\n";
    private const string Done = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
    private const string Limit = "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"busy\"}}\n\n";
    private const string Created = ": heartbeat\n\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"failed-attempt\",\"usage\":{\"input_tokens\":1}}}\n\n";

    [TestMethod]
    [DataRow("codex", "rate")]
    [DataRow("claude", "rate")]
    [DataRow("codex", "429")]
    [DataRow("claude", "429")]
    [DataRow("codex", "empty")]
    [DataRow("claude", "empty")]
    [DataRow("codex", "preamble")]
    [DataRow("claude", "preamble")]
    [DataRow("codex", "io")]
    [DataRow("claude", "io")]
    public async Task PreOutputFailureRetriesSameRequestThenStreamsImmediately(string profile, string failure)
    {
        var firstOutput = Output(profile);
        var done = Finish(profile);
        using var success = new GatedStream(firstOutput, done);
        using var failed = new GatedStream(failure == "empty" ? "" : Created,
            failure == "rate" ? Limit : "", fail: failure == "io");
        failed.Release.TrySetResult();
        var bodies = new List<string>();
        var headers = new List<string>();
        var client = Client(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            headers.Add(string.Join("|", request.Headers.Select(x => x.Key + ":" + string.Join(",", x.Value))));
            if (bodies.Count != 1) return Reply(success);
            return failure == "429"
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\"}}") }
                : Reply(failed);
        });
        var policy = Policy();
        policy.RateLimitRetryEnabled = failure is "rate" or "429";
        policy.EmptyResponseRetryEnabled = !policy.RateLimitRetryEnabled;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, profile, true, policy: policy))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, bodies.Count);
        Assert.AreEqual(1, bodies.Distinct().Count());
        Assert.AreEqual(1, headers.Distinct().Count());
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.IsTrue(result.Response.IsStreaming);
        Assert.IsFalse(success.Disposed);
        if (failure != "429") Assert.IsTrue(failed.Disposed);

        await using var output = result.Response.RawStream!.GetAsyncEnumerator();
        Assert.IsTrue(await output.MoveNextAsync());
        Assert.AreEqual(firstOutput, Encoding.UTF8.GetString(output.Current.Span));
        var next = output.MoveNextAsync().AsTask();
        await success.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(next.IsCompleted);
        success.Release.TrySetResult();
        Assert.IsTrue(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(done, Encoding.UTF8.GetString(output.Current.Span));
        Assert.IsFalse(await output.MoveNextAsync());
        Assert.IsTrue(success.Disposed);
        Assert.AreEqual(2, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task EnabledSwitchesWithZeroRetriesStillStream(bool rate, bool empty)
    {
        using var body = new GatedStream(Text, Done);
        var client = Client(_ => Task.FromResult(Reply(body)));
        var policy = Policy();
        policy.RateLimitRetryEnabled = rate;
        policy.EmptyResponseRetryEnabled = empty;
        policy.ResponseMaxRetries = 0;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(result.Response.IsStreaming);
        Assert.IsFalse(body.Waiting.Task.IsCompleted);
        Assert.IsFalse(body.Disposed);
        await result.Response.Lifetime!.DisposeAsync();
        await result.Response.Lifetime.DisposeAsync();
        Assert.IsTrue(body.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(Created)]
    [DataRow("data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"reasoning\",\"summary\":[]}}\n\n")]
    [DataRow("data: {\"type\":\"message_start\",\"message\":{\"content\":[],\"usage\":{\"input_tokens\":5}}}\n\n")]
    [DataRow("data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n")]
    public async Task MetadataDoesNotEndRetryWindow(string preamble)
    {
        using var body = new GatedStream(preamble, Limit);
        var calls = 0;
        var client = Client(_ => Task.FromResult(++calls == 1
            ? Reply(body) : Reply(new MemoryStream(Encoding.UTF8.GetBytes(Text + Done)))));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var task = terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy()));
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(task.IsCompleted);
        body.Release.TrySetResult();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(Text + Done, await ReadAll(result.Response.RawStream!));
        Assert.AreEqual(2, calls);
        Assert.IsTrue(body.Disposed);
    }

    [TestMethod]
    [DataRow(Text, false)]
    [DataRow(Text, true)]
    [DataRow("data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"thinking\"}\n\n", false)]
    [DataRow("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"thinking\"}}\n\n", true)]
    [DataRow("data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"tool\"}}\n\n", false)]
    [DataRow("data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"id\":\"tool\",\"arguments\":\"\"}}\n\n", true)]
    public async Task AfterOutputRateLimitIsByteExactAndNeverReplayed(string output, bool sameChunk)
    {
        using var body = new GatedStream(output + (sameChunk ? Limit : ""), sameChunk ? "" : Limit);
        body.Release.TrySetResult();
        var client = Client(_ => Task.FromResult(Reply(body)));
        var policy = Policy();
        policy.MaxRetries = 3;
        policy.RetryStatusCodes = "200,429";
        policy.StatusCodeMapping[200] = 201;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy));
        Assert.AreEqual(201, result.Response.StatusCode);
        Assert.IsTrue(result.Response.IsStreaming);
        Assert.AreEqual(output + Limit, await ReadAll(result.Response.RawStream!));
        Assert.AreEqual(1, client.Invocations.Count);
        Assert.IsTrue(body.Disposed);
    }

    [TestMethod]
    [DataRow("\n", 1)]
    [DataRow("\r\n", 1)]
    [DataRow("\r", 7)]
    [DataRow("\n", 16384)]
    public async Task FragmentedUtf8AndSseFramingPreserveAllBytes(string newline, int chunkSize)
    {
        var text = ("\uFEFF: heartbeat\n\nevent: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\ndata: \"delta\":\"你好😀\"}\n\n").Replace("\n", newline);
        var done = Done.Replace("\n", newline);
        using var body = new GatedStream(text, done, chunkSize: chunkSize);
        var client = Client(_ => Task.FromResult(Reply(body)));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(body.Waiting.Task.IsCompleted);
        body.Release.TrySetResult();
        Assert.AreEqual(text + done, await ReadAll(result.Response.RawStream!));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task InvalidBytesAfterOutputArePassedThroughNotReanalyzed()
    {
        var bytes = Encoding.UTF8.GetBytes(Text).Concat(new byte[] { 0xff, 0xfe }).ToArray();
        var client = Client(_ => Task.FromResult(Reply(new MemoryStream(bytes))));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy()));
        using var output = new MemoryStream();
        await foreach (var chunk in result.Response.RawStream!) await output.WriteAsync(chunk);
        CollectionAssert.AreEqual(bytes, output.ToArray());
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task EarlyReaderDisposalClosesUpstreamWithoutReplay()
    {
        using var body = new GatedStream(Text, Done);
        var client = Client(_ => Task.FromResult(Reply(body)));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy()));
        await using (var output = result.Response.RawStream!.GetAsyncEnumerator())
            Assert.IsTrue(await output.MoveNextAsync());
        Assert.IsTrue(body.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task OutputAfterCommitIsNotLimitedByPreOutputBufferSize()
    {
        var suffix = new string('x', 32 * 1024 * 1024 + 1);
        using var body = new GatedStream(Text, suffix);
        body.Release.TrySetResult();
        var client = Client(_ => Task.FromResult(Reply(body)));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy()));
        var count = 0L;
        await foreach (var chunk in result.Response.RawStream!) count += chunk.Length;
        Assert.AreEqual(Encoding.UTF8.GetByteCount(Text) + (long)suffix.Length, count);
        Assert.IsTrue(body.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("idle")]
    [DataRow("total")]
    [DataRow("request")]
    [DataRow("reader")]
    public async Task TimeoutOrCancellationAfterOutputClosesStreamWithoutReplay(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        using var body = new GatedStream(Text, Done);
        var client = Client(_ => Task.FromResult(Reply(body)));
        var policy = Policy();
        if (kind == "idle") policy.StreamIdleTimeoutSeconds = 1;
        if (kind == "total") policy.TotalTimeoutSeconds = 1;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true,
            policy: policy, cancellation: kind == "request" ? cancellation.Token : default));
        await using var output = result.Response.RawStream!.GetAsyncEnumerator(kind == "reader" ? cancellation.Token : default);
        Assert.IsTrue(await output.MoveNextAsync());
        Assert.AreEqual(Text, Encoding.UTF8.GetString(output.Current.Span));
        var next = output.MoveNextAsync().AsTask();
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (kind is "request" or "reader") cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(body.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task NonstreamClientStillWaitsForCompleteResponse()
    {
        using var body = new GatedStream(Text, Done);
        var client = Client(_ => Task.FromResult(Reply(body)));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var task = terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", false, policy: Policy()));
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(task.IsCompleted);
        body.Release.TrySetResult();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(result.Response.IsStreaming);
        Assert.IsNull(result.Response.RawStream);
        Assert.AreEqual("application/json", result.Response.ContentType);
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.IsTrue(body.Disposed);
    }

    private static string Output(string profile) => profile == "codex" ? Text
        : "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}\n\n";
    private static string Finish(string profile) => profile == "codex" ? Done
        : "data: {\"type\":\"message_stop\"}\n\n";
    private static ForwardRequestPolicy Policy() => new()
    {
        RateLimitRetryEnabled = true, EmptyResponseRetryEnabled = true,
        ResponseMaxRetries = 1, ResponseRetryIntervalSeconds = 1
    };
    private static HttpResponseMessage Reply(Stream body) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(body) { Headers = { ContentType = new("text/event-stream") } }
    };
    private static Mock<IPluginHttpClient> Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
    {
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead,
                It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) => reply(request));
        return client;
    }
    private static async Task<string> ReadAll(IAsyncEnumerable<ReadOnlyMemory<byte>> stream)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in stream) await output.WriteAsync(chunk);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class GatedStream(string prefix, string suffix, bool fail = false, int chunkSize = 16384) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        private readonly MemoryStream _suffix = new(Encoding.UTF8.GetBytes(suffix));
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer = buffer[..Math.Min(buffer.Length, chunkSize)];
            var count = await _prefix.ReadAsync(buffer, cancellationToken);
            if (count != 0) return count;
            Waiting.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (fail) throw new IOException("upstream disconnected");
            return await _suffix.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _prefix.Dispose();
            _suffix.Dispose();
            Release.TrySetResult();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
