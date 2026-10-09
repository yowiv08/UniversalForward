using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class StreamCompletionTests
{
    private const string Text = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\n";
    private const string Done = "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"response\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}],\"usage\":{\"input_tokens\":2,\"output_tokens\":1}}}\n\n";
    private const string Message = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"usage\":{\"input_tokens\":2}}}\n\n";
    private const string MessageDone = "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\ndata: {\"type\":\"message_stop\"}\n\n";

    [TestMethod]
    [DataRow("codex", true, true)]
    [DataRow("codex", true, false)]
    [DataRow("codex", false, true)]
    [DataRow("codex", false, false)]
    [DataRow("claude", true, true)]
    [DataRow("claude", true, false)]
    [DataRow("claude", false, true)]
    [DataRow("claude", false, false)]
    public async Task CompleteResponseDoesNotWaitForUpstreamConnectionToClose(string profile, bool stream, bool sameRead)
    {
        var prefix = profile == "codex" ? Text : Message;
        var end = profile == "codex" ? Done : MessageDone;
        using var upstream = new OpenStream(sameRead ? [prefix + end] : [prefix, end]);
        var client = Client(upstream);
        var host = PluginTestHost.Create("universalforward");
        using var logs = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(host) { RequestLogs = logs };
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, profile, stream,
            policy: new() { StreamIdleTimeoutSeconds = 1, TotalTimeoutSeconds = 5 }));
        Assert.AreEqual(200, result.Response.StatusCode);
        if (stream) Assert.AreEqual(prefix + end, await ReadAll(result.Response.RawStream!));
        else
        {
            using var body = JsonDocument.Parse(result.Response.RawContent!);
            Assert.AreEqual(1, body.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        }
        Assert.IsFalse(upstream.ReadPastEnd, "The protocol already ended; another network read can only delay or cancel the completed response.");
        Assert.IsTrue(upstream.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
        await logs.FlushAsync();
        var row = JsonSerializer.SerializeToElement(logs.List(new Dictionary<string, string>())).GetProperty("rows")[0];
        Assert.AreEqual("completed", row.GetProperty("state").GetString());
        Assert.IsFalse(row.TryGetProperty("incomplete", out var incomplete) && incomplete.GetBoolean());
        var detail = logs.Detail(row.GetProperty("id").GetString()!)!;
        var responsePart = detail["parts"]!.AsArray().Single(part => part!["name"]!.ToString() == "attempt-1-response")!;
        Assert.IsFalse(responsePart["truncated"]!.GetValue<bool>());
        Assert.IsFalse(Mock.Get(host.Services.Log).Invocations.Select(call => call.Arguments[0])
            .OfType<PluginLog>().Any(log => log.EventType == "request.stream.failed"));
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public async Task FragmentedTerminalDelimiterCompletesWithoutWaitingForEof(string newline)
    {
        var end = Done.Replace("\n", newline, StringComparison.Ordinal);
        using var upstream = new OpenStream([Text, .. end.Select(character => character.ToString())]);
        var client = Client(upstream);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true,
            policy: new() { StreamIdleTimeoutSeconds = 1, TotalTimeoutSeconds = 5 }));
        Assert.AreEqual(Text + end, await ReadAll(result.Response.RawStream!));
        Assert.IsFalse(upstream.ReadPastEnd);
    }

    [TestMethod]
    [DataRow("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: {\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1}}\n\n", "data: [DONE]\n\n")]
    [DataRow("data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"message\",\"status\":\"completed\"}}\n\n", Text, Done)]
    [DataRow("data: {\"type\":\"content_block_stop\",\"index\":0}\n\n", "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":1}}\n\n",
        "data: {\"type\":\"message_stop\"}\n\n")]
    public async Task ItemAndChoiceCompletionDoNotDiscardRemainingOutputOrUsage(string intermediate, string remaining, string end)
    {
        using var upstream = new OpenStream([Text + intermediate, remaining, end]);
        var client = Client(upstream);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true,
            policy: new() { StreamIdleTimeoutSeconds = 1, TotalTimeoutSeconds = 5 }));
        Assert.AreEqual(Text + intermediate + remaining + end, await ReadAll(result.Response.RawStream!));
        Assert.IsFalse(upstream.ReadPastEnd);
    }

    [TestMethod]
    [DataRow("event: response.completed\ndata: {\"response\":{\"status\":\"completed\"}}\n\n")]
    [DataRow("data: {\"type\":\"response.done\",\"response\":{\"status\":\"completed\"}}\n\n")]
    public async Task CompatibleResponseEndEventsAlsoCloseTheStream(string end)
    {
        using var upstream = new OpenStream([Text, end]);
        var client = Client(upstream);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true,
            policy: new() { StreamIdleTimeoutSeconds = 1, TotalTimeoutSeconds = 5 }));
        Assert.AreEqual(Text + end, await ReadAll(result.Response.RawStream!));
        Assert.IsFalse(upstream.ReadPastEnd);
    }

    private static Mock<IPluginHttpClient> Client(Stream upstream)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(upstream) { Headers = { ContentType = new("text/event-stream") } } });
        return client;
    }

    private static async Task<string> ReadAll(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in chunks) await output.WriteAsync(chunk);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    // A server may leave an HTTP response open after its final SSE event.
    private sealed class OpenStream(string[] chunks) : Stream
    {
        private readonly Queue<byte[]> _chunks = new(chunks.Select(Encoding.UTF8.GetBytes));
        private int _offset;
        internal bool ReadPastEnd { get; private set; }
        internal bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_chunks.TryPeek(out var chunk))
            {
                ReadPastEnd = true;
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            var count = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == chunk.Length) { _chunks.Dequeue(); _offset = 0; }
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
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
