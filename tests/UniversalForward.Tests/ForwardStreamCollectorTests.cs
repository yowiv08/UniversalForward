using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ForwardStreamCollectorTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(4096)]
    public async Task ResponsesKeepsNativeResponseAndUtf8AcrossChunks(int chunkSize)
    {
        const string response = """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"你好"}]},{"type":"function_call","arguments":"{\"x\":1}"}],"usage":{"input_tokens":1,"output_tokens":2},"unknown":"keep"}""";
        var frames = ": ping\r\n\r\nevent: response.completed\r\ndata: {\"type\":\"response.completed\",\r\ndata: \"response\":" + response + "}\r\n\r\n";
        var bytes = await ForwardStreamCollector.CollectAsync(Chunks(frames, chunkSize), "/v1/responses", CancellationToken.None);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(response), JsonNode.Parse(bytes)));
    }

    [TestMethod]
    public async Task ClaudeAssemblesTextThinkingToolsAndUsage()
    {
        const string frames = """
            data: {"type":"message_start","message":{"id":"m","type":"message","role":"assistant","model":"future","content":[],"usage":{"input_tokens":19,"cache_read_input_tokens":8}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"thinking"}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"你好"}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"citations_delta","citation":{"type":"page_location","document_index":0}}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"tool","name":"lookup","input":{}}}

            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"q\":"}}

            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"你好\"}"}}

            data: {"type":"content_block_stop","index":2}

            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":10}}

            data: {"type":"message_stop"}
            """;
        var bytes = await ForwardStreamCollector.CollectAsync(Chunks(frames + "\n\n", 1), "/v1/messages", CancellationToken.None);
        var message = JsonNode.Parse(bytes)!;
        Assert.AreEqual("signed", message["content"]![0]!["signature"]!.ToString());
        Assert.AreEqual("你好", message["content"]![1]!["text"]!.ToString());
        Assert.AreEqual(1, message["content"]![1]!["citations"]!.AsArray().Count);
        Assert.AreEqual("你好", message["content"]![2]!["input"]!["q"]!.ToString());
        Assert.AreEqual("tool_use", message["stop_reason"]!.ToString());
        Assert.AreEqual(19, message["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.AreEqual(8, message["usage"]!["cache_read_input_tokens"]!.GetValue<int>());
        Assert.AreEqual(10, message["usage"]!["output_tokens"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task LongResponsesEventAcrossChunksUsesBoundedAllocations()
    {
        await ForwardStreamCollector.CollectAsync(
            Chunks("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n", 7),
            "/v1/responses", CancellationToken.None);
        var text = new string('x', 1024 * 1024) + "你好😀";
        var frames = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\""
            + text + "\"}]}],\"unknown\":\"keep\"}}\n\n";
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        // This in-memory source completes synchronously, keeping the allocation measurement on one thread.
        var collection = ForwardStreamCollector.CollectAsync(Chunks(frames, 4093), "/v1/responses", CancellationToken.None);
        Assert.IsTrue(collection.IsCompletedSuccessfully);
        var bytes = await collection;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var response = JsonNode.Parse(bytes)!;
        Assert.AreEqual(text, response["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("keep", response["unknown"]!.GetValue<string>());
        // Re-copying the unfinished line on each chunk allocates over 250 MiB for this input.
        Assert.IsLessThan(96L * 1024 * 1024, allocated, $"Collector allocated {allocated} bytes for a 1 MiB event.");
    }

    [TestMethod]
    public async Task ClaudeInterleavedDeltasKeepInitialContentWithBoundedAllocations()
    {
        const string start = "data: {\"type\":\"message_start\",\"message\":{\"type\":\"message\",\"content\":[]}}\n\n";
        const string end = "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n";
        await ForwardStreamCollector.CollectAsync(Chunks(start + end, 7), "/v1/messages", CancellationToken.None);
        const int count = 2048;
        var textDelta = new string('x', 256);
        var thinkingDelta = new string('y', 128);
        var signatureDelta = new string('z', 64);
        var frames = new StringBuilder(start);
        frames.Append("data: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"text\",\"text\":\"prefix-你好\",\"unknown\":\"keep\"}}\n\n");
        frames.Append("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"initial-thinking-\",\"signature\":\"initial-signature-\"}}\n\n");
        var textEvent = Delta(2, "text_delta", "text", textDelta);
        var thinkingEvent = Delta(0, "thinking_delta", "thinking", thinkingDelta);
        var signatureEvent = Delta(0, "signature_delta", "signature", signatureDelta);
        for (var i = 0; i < count; i++) frames.Append(textEvent).Append(thinkingEvent).Append(signatureEvent);
        frames.Append("data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"untouched\"}}\n\n");
        foreach (var index in new[] { 2, 1, 0 })
            frames.Append("data: {\"type\":\"content_block_stop\",\"index\":").Append(index).Append("}\n\n");
        frames.Append(end);
        var input = frames.ToString();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var collection = ForwardStreamCollector.CollectAsync(Chunks(input, 4093), "/v1/messages", CancellationToken.None);
        Assert.IsTrue(collection.IsCompletedSuccessfully);
        var bytes = await collection;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var content = JsonNode.Parse(bytes)!["content"]!.AsArray();
        Assert.HasCount(3, content);
        Assert.AreEqual("initial-thinking-" + string.Concat(Enumerable.Repeat(thinkingDelta, count)), content[0]!["thinking"]!.GetValue<string>());
        Assert.AreEqual("initial-signature-" + string.Concat(Enumerable.Repeat(signatureDelta, count)), content[0]!["signature"]!.GetValue<string>());
        Assert.AreEqual("untouched", content[1]!["text"]!.GetValue<string>());
        Assert.AreEqual("prefix-你好" + string.Concat(Enumerable.Repeat(textDelta, count)), content[2]!["text"]!.GetValue<string>());
        Assert.AreEqual("keep", content[2]!["unknown"]!.GetValue<string>());
        // Rebuilding accumulated strings for each delta allocates over 1 GiB for this input.
        Assert.IsLessThan(128L * 1024 * 1024, allocated, $"Collector allocated {allocated} bytes for interleaved deltas.");

        static string Delta(int index, string type, string property, string value)
            => "data: " + JsonSerializer.Serialize(new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = type, [property] = value }
            }) + "\n\n";
    }

    [TestMethod]
    [DataRow("/v1/responses", "data: {\"type\":\"response.created\"}\n\n")]
    [DataRow("/v1/responses", "data: [DONE]\n\n")]
    [DataRow("/v1/responses", "data: {\"type\":\"response.completed\"}\n\n")]
    [DataRow("/v1/responses", "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"failed\"}}}\n\n")]
    [DataRow("/v1/messages", "data: {\"type\":\"error\",\"error\":{\"message\":\"overloaded\"}}\n\n")]
    [DataRow("/v1/messages", "data: {\"type\":\"message_stop\"}\n\n")]
    [DataRow("/v1/messages", "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"bad\"}}\n\n")]
    [DataRow("/v1/messages", "data: [not json]\n\n")]
    public async Task MalformedOrTruncatedStreamsAreNotSuccessful(string endpoint, string frames)
        => await Assert.ThrowsAsync<IOException>(() => ForwardStreamCollector.CollectAsync(Chunks(frames, 7), endpoint, CancellationToken.None));

    [TestMethod]
    public async Task TokenLimitedResponsesRemainIncomplete()
    {
        var bytes = await ForwardStreamCollector.CollectAsync(
            Chunks("data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"output\":[],\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n", 64),
            "/v1/responses", CancellationToken.None);
        Assert.AreEqual("incomplete", JsonNode.Parse(bytes)!["status"]!.ToString());
    }

    [TestMethod]
    public async Task CancellationIsRespected()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ForwardStreamCollector.CollectAsync(
            Chunks(": ping\n\n", 2), "/v1/responses", source.Token));
    }

    [TestMethod]
    public async Task OversizedResponsesAreRejected()
        => await Assert.ThrowsAsync<IOException>(() => ForwardStreamCollector.CollectAsync(
            Oversized(), "/v1/responses", CancellationToken.None));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Oversized()
    {
        yield return new byte[32 * 1024 * 1024 + 1];
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(string frames, int size,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(frames);
        for (var i = 0; i < bytes.Length; i += size)
        {
            ct.ThrowIfCancellationRequested();
            yield return bytes.AsMemory(i, Math.Min(size, bytes.Length - i));
        }
        await Task.CompletedTask;
    }
}
