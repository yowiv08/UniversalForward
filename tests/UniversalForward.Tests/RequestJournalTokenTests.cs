using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plugins.UniversalForward;
using Router.Contracts.Domain;

namespace UniversalForward.Tests;

[TestClass]
public sealed class RequestJournalTokenTests
{
    private static RequestLogCapture Begin(RequestLogStore store, string endpoint = "/v1/responses")
        => store.Begin("forward", "channel", "Channel", "model", endpoint, "direct", "trace", null, "{}", null)!;

    private static async Task Consume(RequestLogCapture capture, int number, string body, string mediaType = "application/json")
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        capture.Received(number, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var bytes = new byte[7];
        int read;
        while ((read = await stream.ReadAsync(bytes)) > 0) output.Write(bytes, 0, read);
        Assert.AreEqual(body, Encoding.UTF8.GetString(output.ToArray()));
    }

    private static void AssertTokens(JsonNode? tokenUsage, long? input, long? output, long? read, long? write)
    {
        Assert.IsNotNull(tokenUsage);
        Assert.AreEqual(input, tokenUsage["inputTokens"]?.GetValue<long>());
        Assert.AreEqual(output, tokenUsage["outputTokens"]?.GetValue<long>());
        Assert.AreEqual(read, tokenUsage["cacheReadTokens"]?.GetValue<long>());
        Assert.AreEqual(write, tokenUsage["cacheWriteTokens"]?.GetValue<long>());
        Assert.AreEqual(4, tokenUsage.AsObject().Count);
    }

    [TestMethod]
    public async Task ClaudeJsonUsageIncludesReadAndWriteCacheInInputTotal()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "/v1/messages");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var number = capture.Sending(request, "{}"u8);
        const string body = """{"usage":{"input_tokens":3,"output_tokens":178,"cache_read_input_tokens":125712,"cache_creation_input_tokens":2977},"content":[{"type":"text","text":"answer"}]}""";
        await Consume(capture, number, body);
        capture.Complete("completed", 200);
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        AssertTokens(detail["tokenUsage"], 128692, 178, 125712, 2977);
        AssertTokens(detail["attempts"]![0]!["tokenUsage"], 128692, 178, 125712, 2977);
        Assert.IsFalse(detail["parts"]!.AsArray().Any(part =>
            RequestLogStore.IsUpstreamBodyPart(part!["name"]!.ToString())));
    }

    [TestMethod]
    [DataRow("{\"input_tokens\":128692,\"output_tokens\":178,\"input_tokens_details\":{\"cached_tokens\":125712}}")]
    [DataRow("{\"prompt_tokens\":128692,\"completion_tokens\":178,\"prompt_tokens_details\":{\"cached_tokens\":125712}}")]
    public async Task OpenAiInputAlreadyIncludesCache(string usage)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        await Consume(capture, number, "{\"usage\":" + usage + "}");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 128692, 178, 125712, null);
    }

    [TestMethod]
    public async Task OpenAiUsageShapeDoesNotAddCacheWhenTheRequestedEndpointIsMessages()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "/v1/messages");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var number = capture.Sending(request, "{}"u8);
        await Consume(capture, number, "{\"usage\":{\"input_tokens\":100,\"output_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":25},\"cache_creation_input_tokens\":4}}");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 8, 25, 4);
    }

    [TestMethod]
    [DataRow("/v1/messages", "/v1/responses", 100L)]
    [DataRow("/v1/responses", "/v1/messages", 104L)]
    [DataRow("/v1/messages", "/", 104L)]
    [DataRow("/v1/responses", "/", 100L)]
    public async Task ActualUpstreamEndpointDeterminesInputCacheMeaningWhenUsageHasNoDetails(
        string incomingEndpoint, string upstreamPath, long input)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, incomingEndpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example" + upstreamPath);
        var number = capture.Sending(request, "{}"u8);
        await Consume(capture, number, "{\"usage\":{\"input_tokens\":100,\"output_tokens\":8,\"cache_creation_input_tokens\":4}}");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], input, 8, null, 4);
    }

    [TestMethod]
    public async Task ResponsesCompletionAndExplicitCacheWriteUseOnlyDeclaredCounters()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        const string body = "data: {\"type\":\"response.created\",\"response\":{\"model\":\"upstream\"}}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":100,\"output_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":25},\"cache_creation_input_tokens\":4}}}\n\n";
        await Consume(capture, number, body, "text/event-stream");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 8, 25, 4);
    }

    [TestMethod]
    [DataRow("input_tokens_details", "null")]
    [DataRow("input_tokens_details", "[]")]
    [DataRow("input_tokens_details", "false")]
    [DataRow("prompt_tokens_details", "null")]
    [DataRow("prompt_tokens_details", "[]")]
    [DataRow("prompt_tokens_details", "false")]
    public async Task ExplicitInvalidCacheContainerClearsCacheWhileMissingFieldsPreserveIt(string container, string invalid)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        await Consume(capture, number, "{\"usage\":{\"input_tokens\":100,\"output_tokens\":8,\"" + container + "\":{\"cached_tokens\":25}}}");
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 8, 25, null);

        await Consume(capture, number, "{\"usage\":{\"output_tokens\":9}}");
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 9, 25, null);

        await Consume(capture, number, "{\"usage\":{\"" + container + "\":" + invalid + "}}");
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 9, null, null);
        AssertTokens(store.Detail(capture.Id)!["attempts"]![0]!["tokenUsage"], 100, 9, null, null);

        await Consume(capture, number, "{\"usage\":{\"" + container + "\":{\"cached_tokens\":30}}}");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 9, 30, null);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("false")]
    [DataRow("\"invalid\"")]
    public async Task ExplicitInvalidUsageClearsLatestCountersAndLaterValidUsageRestoresThem(string invalid)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        const string valid = "{\"usage\":{\"input_tokens\":100,\"output_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":25},\"cache_creation_input_tokens\":4}}";
        await Consume(capture, number, valid);
        await Consume(capture, number, "{\"model\":\"upstream\"}");
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 8, 25, 4);

        await Consume(capture, number, "{\"usage\":" + invalid + "}");
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], null, null, null, null);
        AssertTokens(store.Detail(capture.Id)!["attempts"]![0]!["tokenUsage"], null, null, null, null);

        await Consume(capture, number, valid);
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 8, 25, 4);
    }

    [TestMethod]
    public async Task ClaudeSplitEventsOverrideCountersWithoutAddingRepeatedSnapshots()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "/v1/messages");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var number = capture.Sending(request, "{}"u8);
        const string start = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":0,\"cache_read_input_tokens\":125712,\"cache_creation_input_tokens\":2977}}}\n\n";
        const string partial = "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":40}}\n\n";
        const string final = "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":178}}\n\n";
        await Consume(capture, number, start + start + partial + final + final, "text/event-stream");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 128692, 178, 125712, 2977);
    }

    [TestMethod]
    [DataRow("{}", null, null, null, null)]
    [DataRow("{\"input_tokens\":0,\"output_tokens\":0,\"input_tokens_details\":{\"cached_tokens\":0},\"cache_creation_input_tokens\":0}", 0L, 0L, 0L, 0L)]
    [DataRow("{\"input_tokens\":3}", 3L, null, null, null)]
    [DataRow("{\"input_tokens\":-1,\"output_tokens\":\"178\",\"input_tokens_details\":{\"cached_tokens\":false},\"cache_creation_input_tokens\":null}", null, null, null, null)]
    [DataRow("{\"input_tokens\":2.5,\"output_tokens\":178,\"input_tokens_details\":{\"cached_tokens\":-1},\"cache_creation_input_tokens\":[]}", null, 178L, null, null)]
    public async Task MissingZeroAndInvalidCountsStayDistinct(string usage, long? input, long? output, long? read, long? write)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        await Consume(capture, number, "{\"usage\":" + usage + "}");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], input, output, read, write);
    }

    [TestMethod]
    public async Task InvalidClaudeCacheCannotProduceAFalseInputTotal()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "/v1/messages");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var number = capture.Sending(request, "{}"u8);
        await Consume(capture, number, "{\"usage\":{\"input_tokens\":3,\"output_tokens\":178,\"cache_read_input_tokens\":\"125712\",\"cache_creation_input_tokens\":2977}}");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], null, 178, null, 2977);
    }

    [TestMethod]
    public async Task ClaudeCacheCountsCanArriveInSeparateEvents()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "/v1/messages");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var number = capture.Sending(request, "{}"u8);
        const string body = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3}}}\n\n"
            + "data: {\"type\":\"message_delta\",\"usage\":{\"cache_read_input_tokens\":125712}}\n\n"
            + "data: {\"type\":\"message_delta\",\"usage\":{\"cache_creation_input_tokens\":2977,\"output_tokens\":178}}\n\n";
        await Consume(capture, number, body, "text/event-stream");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 128692, 178, 125712, 2977);
    }

    [TestMethod]
    public async Task ClaudeInputSumOverflowStaysUnknown()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "/v1/messages");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var number = capture.Sending(request, "{}"u8);
        await Consume(capture, number, "{\"usage\":{\"input_tokens\":9223372036854775807,\"cache_read_input_tokens\":1,\"output_tokens\":0}}");
        capture.Complete("completed", 200);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], null, 0, 1, null);
    }

    [TestMethod]
    public async Task NewAttemptResetsUsageAndLateOldEventsOnlyUpdateOldAttempt()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "/v1/messages");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var first = capture.Sending(request, "{}"u8);
        const string prefix = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":0,\"cache_read_input_tokens\":125712,\"cache_creation_input_tokens\":2977}}}\n\n";
        const string late = "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":178}}\n\n";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(prefix + late, Encoding.UTF8, "text/event-stream") };
        capture.Received(first, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadExactlyAsync(new byte[Encoding.UTF8.GetByteCount(prefix)]);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 128692, 0, 125712, 2977);

        var second = capture.Sending(request, "{}"u8);
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], null, null, null, null);
        await Consume(capture, second, "{\"usage\":{\"input_tokens\":2,\"output_tokens\":4,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}");
        await stream.CopyToAsync(Stream.Null);
        capture.Complete("completed", 200);
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        AssertTokens(detail["tokenUsage"], 2, 4, 0, 0);
        AssertTokens(detail["attempts"]![0]!["tokenUsage"], 128692, 178, 125712, 2977);
        AssertTokens(detail["attempts"]![1]!["tokenUsage"], 2, 4, 0, 0);
    }

    [TestMethod]
    public async Task RepeatedUsageEventsDoNotFillTheMetadataQueue()
    {
        using var store = new RequestLogStore(32);
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        const string frame = "data: {\"response\":{\"usage\":{\"input_tokens\":100,\"output_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":25}}}}\n\n";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(string.Concat(Enumerable.Repeat(frame, 64)), Encoding.UTF8, "text/event-stream") };
        capture.Received(number, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadExactlyAsync(new byte[Encoding.UTF8.GetByteCount(frame)]);
        await store.FlushAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Assert.IsTrue(store.Enqueue("", _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        try { await stream.CopyToAsync(Stream.Null); capture.Complete("completed", 200); }
        finally { release.Set(); }
        await store.FlushAsync();
        Assert.AreEqual(0L, JsonSerializer.SerializeToElement(store.Status, RequestLogStore.Json)
            .GetProperty("droppedWrites").GetInt64());
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 100, 8, 25, null);
    }

    [TestMethod]
    public async Task AdapterUsageFillsUnknownInputOutputWithoutOverwritingPreciseUsage()
    {
        using var store = new RequestLogStore();
        var fallback = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        fallback.Sending(request, "{}"u8);
        fallback.Attach(new AdapterResponse { StatusCode = 200, Usage = new Usage(9, 4, 13) });
        var precise = Begin(store, "/v1/messages");
        var number = precise.Sending(request, "{}"u8);
        await Consume(precise, number, "{\"usage\":{\"input_tokens\":3,\"output_tokens\":178,\"cache_read_input_tokens\":125712,\"cache_creation_input_tokens\":2977}}");
        precise.Attach(new AdapterResponse { StatusCode = 200, Usage = new Usage(1, 2, 3) });
        await store.FlushAsync();
        AssertTokens(store.Detail(fallback.Id)!["tokenUsage"], 9, 4, null, null);
        AssertTokens(store.Detail(precise.Id)!["tokenUsage"], 128692, 178, 125712, 2977);
    }

    [TestMethod]
    public async Task OversizeResponseUsageFallsBackToAdapterTotalsWithUnknownCaches()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        var body = "{\"usage\":{\"input_tokens\":12,\"output_tokens\":7,\"input_tokens_details\":{\"cached_tokens\":5}},\"output\":\""
            + new string('x', 1024 * 1024) + "\"}";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        capture.Received(number, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        await stream.CopyToAsync(Stream.Null);
        capture.Attach(new AdapterResponse { StatusCode = 200, Usage = new Usage(12, 7, 19) });
        await store.FlushAsync();
        AssertTokens(store.Detail(capture.Id)!["tokenUsage"], 12, 7, null, null);
    }

    [TestMethod]
    public async Task WebSocketConvertedEventsReportUsageWithoutSavingEvents()
    {
        const string completed = """{"type":"response.completed","response":{"status":"completed","output":[],"usage":{"input_tokens":12,"output_tokens":7,"input_tokens_details":{"cached_tokens":5}}}}""";
        const string payload = """{"type":"response.create","model":"model","input":"question"}""";
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            Assert.AreEqual(payload, await WebSocketLoopback.ReceiveAsync(socket, ct));
            await WebSocketLoopback.SendAsync(socket, completed, ct, fragmentSize: 1);
        });
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Address);
        UpstreamWebSocket.PrepareHandshake(request);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var number = capture.Sending(request, bytes, "websocket");
        using var client = server.CreateClient();
        using var response = await UpstreamWebSocket.SendAsync(client, request, bytes, capture, number, CancellationToken.None);
        capture.Received(number, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        await stream.CopyToAsync(Stream.Null);
        capture.Complete("completed", 200);
        await server.Completion;
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        AssertTokens(detail["tokenUsage"], 12, 7, 5, null);
        Assert.IsFalse(detail["parts"]!.AsArray().Any(part =>
            RequestLogStore.IsUpstreamBodyPart(part!["name"]!.ToString())));
    }
}
