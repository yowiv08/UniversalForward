using System.Runtime.CompilerServices;
using System.Text;
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
