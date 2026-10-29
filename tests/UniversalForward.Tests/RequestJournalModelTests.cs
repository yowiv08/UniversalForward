using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class RequestJournalModelTests
{
    private static RequestLogCapture Begin(RequestLogStore store, string? incoming = null)
        => store.Begin("forward", "channel", "Channel", "routed-model", "/v1/responses", "direct", "trace",
            null, incoming is null ? null : JsonSerializer.Deserialize<JsonElement>(incoming), null)!;

    private static async Task<string> ReadFragmented(HttpResponseMessage response, int bufferSize = 3)
    {
        using var source = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0) output.Write(buffer, 0, read);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<string> ReadPart(RequestLogStore store, string id, string part)
    {
        await store.FlushAsync();
        using var body = new MemoryStream();
        var after = -1;
        while (true)
        {
            var result = JsonSerializer.SerializeToElement(store.ReadPart(id, part, after), RequestLogStore.Json);
            foreach (var chunk in result.GetProperty("chunks").EnumerateArray())
                body.Write(Convert.FromBase64String(chunk.GetProperty("base64").GetString()!));
            if (result.GetProperty("done").GetBoolean()) break;
            after = result.GetProperty("next").GetInt32();
        }
        return Encoding.UTF8.GetString(body.ToArray());
    }
    private static void AssertNoUpstreamBodies(RequestLogStore store, string id)
    {
        var names = store.Detail(id)!["parts"]!.AsArray().Select(part => part!["name"]!.ToString());
        foreach (var name in names)
            Assert.IsFalse(RequestLogStore.IsBodyPart(name), "Unexpected retained upstream body: " + name);
    }

    [TestMethod]
    public async Task ForwardLogDistinguishesLocalSentAndReportedModelsWithoutReasoning()
    {
        using var store = new RequestLogStore();
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "secret" }], 0);
        const string body = """{"model":"upstream-snapshot","output":[{"type":"message","content":[{"type":"output_text","text":"answer"}]}]}""";
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account)) { RequestLogs = store };
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(new { model = "local-alias", input = "question" });
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(body, Encoding.UTF8.GetString(result.Response.RawContent!));
        await store.FlushAsync();
        var row = JsonSerializer.SerializeToElement(store.List(new Dictionary<string, string>()), RequestLogStore.Json)
            .GetProperty("rows")[0];
        Assert.AreEqual("model", row.GetProperty("model").GetString(), "Existing filter metadata is preserved.");
        Assert.AreEqual("local-alias", row.GetProperty("requestedModel").GetString());
        Assert.AreEqual("model", row.GetProperty("sentModel").GetString());
        Assert.AreEqual("upstream-snapshot", row.GetProperty("reportedModel").GetString());
        var id = row.GetProperty("id").GetString()!;
        var detail = store.Detail(id)!;
        Assert.AreEqual("model", detail["attempts"]![0]!["sentModel"]!.ToString());
        Assert.AreEqual("upstream-snapshot", detail["attempts"]![0]!["reportedModel"]!.ToString());
        Assert.AreEqual("", await ReadPart(store, id, "attempt-1-response"));
        AssertNoUpstreamBodies(store, id);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{}")]
    [DataRow("{\"model\":null}")]
    [DataRow("{\"model\":42}")]
    [DataRow("{\"model\":\" \"}")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("42")]
    public async Task MissingLocalModelUsesHostModelWithoutInventingSentOrReportedModel(string? incoming)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, incoming);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"output\":[]}", Encoding.UTF8, "application/json") };
        capture.Received(number, response);
        await ReadFragmented(response);
        capture.Complete("completed", 200);
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        Assert.AreEqual("routed-model", detail["requestedModel"]!.ToString());
        Assert.IsNull(detail["sentModel"]);
        Assert.IsNull(detail["reportedModel"]);
    }

    [TestMethod]
    [DataRow("{\"model\":\"上游😀\",\"output\":[]}", "application/json")]
    [DataRow("{\"model\":\"上游😀\",\"output\":[]}", null)]
    [DataRow("data: {\"type\":\"response.created\",\"response\":{\"model\":\"上游😀\"}}\r\n\r\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\r\n\r\n", "text/event-stream")]
    [DataRow("data: {\"model\":\"上游😀\",\"choices\":[]}\n\n", "Text/Event-Stream")]
    [DataRow("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"上游😀\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", "text/event-stream")]
    [DataRow("data: {\"model\":\"上游😀\",\"choices\":[]}\n\ndata: [DONE]\n\n", "text/event-stream")]
    [DataRow("\uFEFF: heartbeat\rdata: {\"model\":\"上游😀\",\rdata: \"choices\":[]}\r\r", "text/event-stream")]
    [DataRow("data: {\"model\":\"上游😀\",\r\ndata: \"choices\":[]}\r\n\r\n", "text/event-stream")]
    public async Task FragmentedResponsesCaptureDeclaredModelAndPreserveEveryByte(string body, string? mediaType)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "{\"model\":\"local-model\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{\"model\":\"sent-model\"}"u8);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        if (mediaType is null) response.Content.Headers.ContentType = null;
        capture.Received(number, response);
        Assert.AreEqual(body, await ReadFragmented(response));
        capture.Complete("completed", 200);
        Assert.AreEqual("", await ReadPart(store, capture.Id, "attempt-1-response"));
        AssertNoUpstreamBodies(store, capture.Id);
        var detail = store.Detail(capture.Id)!;
        Assert.AreEqual("上游😀", detail["reportedModel"]!.ToString());
        Assert.AreEqual("上游😀", detail["attempts"]![0]!["reportedModel"]!.ToString());
    }

    [TestMethod]
    public async Task UnterminatedSseFrameDoesNotReportMetadataAtEof()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        const string body = "data: {\"model\":\"unfinished\"}\n";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        capture.Received(number, response);
        Assert.AreEqual(body, await ReadFragmented(response));
        capture.Complete("completed", 200);
        Assert.AreEqual("", await ReadPart(store, capture.Id, "attempt-1-response"));
        AssertNoUpstreamBodies(store, capture.Id);
        Assert.IsNull(store.Detail(capture.Id)!["reportedModel"]);
    }

    [TestMethod]
    public async Task OversizeSseMetadataKeepsPassthroughAndResumesLaterMetadata()
    {
        using var store = new RequestLogStore(4096);
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        const string first = "data: {\"response\":{\"model\":\"initial\",\"reasoning\":{\"effort\":\"low\"}}}\n\n";
        var body = first + "data: {\"model\":\"oversize\",\"padding\":\"" + new string('x', 1024 * 1024)
            + "\"}\n\ndata: {\"model\":\"later\"}\n\n";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        capture.Received(number, response);
        Assert.AreEqual(body, await ReadFragmented(response, 4096));
        capture.Complete("completed", 200);
        Assert.AreEqual("", await ReadPart(store, capture.Id, "attempt-1-response"));
        AssertNoUpstreamBodies(store, capture.Id);
        var detail = store.Detail(capture.Id)!;
        Assert.AreEqual("later", detail["reportedModel"]!.ToString());
        Assert.AreEqual("low", detail["reportedReasoning"]!["reasoning.effort"]!.ToString());
        Assert.IsFalse(detail["incomplete"]?.GetValue<bool>() ?? false,
            "Skipping oversized metadata must not mark request metadata or headers incomplete.");
    }

    [TestMethod]
    public async Task OversizeJsonMetadataDoesNotInterruptPassthrough()
    {
        using var store = new RequestLogStore(4096);
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        var body = "{\"model\":\"oversize\",\"padding\":\"" + new string('x', 1024 * 1024) + "\"}";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        capture.Received(number, response);
        Assert.AreEqual(body, await ReadFragmented(response, 8192));
        capture.Complete("completed", 200);
        Assert.AreEqual("", await ReadPart(store, capture.Id, "attempt-1-response"));
        AssertNoUpstreamBodies(store, capture.Id);
        Assert.IsNull(store.Detail(capture.Id)!["reportedModel"]);
    }

    [TestMethod]
    public async Task InvalidUtf8InSseMetadataDoesNotChangeRawBytes()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        var body = "data: {\"model\":\""u8.ToArray().Concat(new byte[] { 0xff })
            .Concat("\"}\n\n"u8.ToArray()).ToArray();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        capture.Received(number, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        CollectionAssert.AreEqual(body, output.ToArray());
        capture.Complete("completed", 200);
        await store.FlushAsync();
        Assert.AreEqual("\uFFFD", store.Detail(capture.Id)!["reportedModel"]!.ToString());
        AssertNoUpstreamBodies(store, capture.Id);
    }

    [TestMethod]
    public async Task RepeatedSseMetadataDoesNotEnqueueRedundantSnapshots()
    {
        using var store = new RequestLogStore(32);
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{}"u8);
        const string frame = "data: {\"response\":{\"model\":\"same-model\",\"reasoning\":{\"effort\":\"low\"}}}\n\n";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(string.Concat(Enumerable.Repeat(frame, 64)), Encoding.UTF8, "text/event-stream") };
        capture.Received(number, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        var prefix = new byte[Encoding.UTF8.GetByteCount(frame)];
        await stream.ReadExactlyAsync(prefix);
        await store.FlushAsync();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Assert.IsTrue(store.Enqueue("", _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await stream.CopyToAsync(Stream.Null);
            capture.Complete("completed", 200);
        }
        finally { release.Set(); }
        await store.FlushAsync();
        var status = JsonSerializer.SerializeToElement(store.Status, RequestLogStore.Json);
        Assert.AreEqual(0L, status.GetProperty("droppedWrites").GetInt64(),
            "Unchanged model/reasoning in every SSE event must not fill the metadata queue.");
        var detail = store.Detail(capture.Id)!;
        Assert.AreEqual("same-model", detail["reportedModel"]!.ToString());
        Assert.AreEqual("low", detail["reportedReasoning"]!["reasoning.effort"]!.ToString());
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"model\":null}")]
    [DataRow("{\"model\":false}")]
    [DataRow("{\"model\":{\"name\":\"fake\"}}")]
    [DataRow("{\"model\":[]}")]
    [DataRow("{\"model\":\" \"}")]
    [DataRow("malformed")]
    public async Task MissingOrInvalidResponseModelIsNotReplacedByRequestModel(string body)
    {
        using var store = new RequestLogStore();
        var capture = Begin(store, "{\"model\":\"local-model\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var number = capture.Sending(request, "{\"model\":\"sent-model\"}"u8);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        capture.Received(number, response);
        Assert.AreEqual(body, await ReadFragmented(response));
        capture.Complete("completed", 200);
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        Assert.AreEqual("sent-model", detail["sentModel"]!.ToString());
        Assert.IsNull(detail["reportedModel"]);
        Assert.IsNull(detail["attempts"]![0]!["reportedModel"]);
    }

    [TestMethod]
    public async Task RetryClearsPreviousReportedModelAndLateOldResponseCannotOverwriteSummary()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var first = capture.Sending(request, "{\"model\":\"first-sent\"}"u8);
        const string firstFrame = "data: {\"response\":{\"model\":\"first-reported\",\"reasoning\":{\"effort\":\"low\"}}}\n\n";
        const string lateFrame = "data: {\"response\":{\"model\":\"late-old-model\"}}\n\n";
        using var firstResponse = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(firstFrame + lateFrame, Encoding.UTF8, "text/event-stream") };
        capture.Received(first, firstResponse);
        using var firstStream = await firstResponse.Content.ReadAsStreamAsync();
        var prefix = new byte[Encoding.UTF8.GetByteCount(firstFrame)];
        await firstStream.ReadExactlyAsync(prefix);
        await store.FlushAsync();
        Assert.AreEqual("first-reported", store.Detail(capture.Id)!["reportedModel"]!.ToString());

        capture.Retry("empty response");
        var second = capture.Sending(request, "{\"model\":\"second-sent\"}"u8);
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        Assert.IsNull(detail["reportedModel"]);
        Assert.IsNull(detail["reportedReasoning"]);
        Assert.AreEqual("first-reported", detail["attempts"]![0]!["reportedModel"]!.ToString());

        using var secondResponse = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"model\":\"second-reported\"}", Encoding.UTF8, "application/json") };
        capture.Received(second, secondResponse);
        await ReadFragmented(secondResponse);
        await firstStream.CopyToAsync(Stream.Null);
        capture.Complete("completed", 200);
        await store.FlushAsync();
        detail = store.Detail(capture.Id)!;
        Assert.AreEqual("second-sent", detail["sentModel"]!.ToString());
        Assert.AreEqual("second-reported", detail["reportedModel"]!.ToString());
        Assert.AreEqual("late-old-model", detail["attempts"]![0]!["reportedModel"]!.ToString());
        Assert.AreEqual("second-reported", detail["attempts"]![1]!["reportedModel"]!.ToString());
    }

    [TestMethod]
    public async Task ComplementaryReasoningFieldsMergeWithoutLeakingAcrossAttempts()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/messages");
        var first = capture.Sending(request, "{}"u8);
        const string start = "data: {\"type\":\"message_start\",\"message\":{\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":1024}}}\n\n";
        const string delta = "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens_details\":{\"reasoning_tokens\":10}}}\n\n";
        const string changed = "data: {\"type\":\"message_delta\",\"thinking\":{\"budget_tokens\":2048}}\n\n";
        const string late = "data: {\"type\":\"message_delta\",\"thinking\":{\"effort\":\"high\"},\"usage\":{\"output_tokens_details\":{\"reasoning_tokens\":11}}}\n\n";
        using var firstResponse = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(start + delta + changed + late, Encoding.UTF8, "text/event-stream") };
        capture.Received(first, firstResponse);
        using var firstStream = await firstResponse.Content.ReadAsStreamAsync();
        await firstStream.ReadExactlyAsync(new byte[Encoding.UTF8.GetByteCount(start + delta + changed)]);
        await store.FlushAsync();
        var reasoning = store.Detail(capture.Id)!["reportedReasoning"]!;
        Assert.AreEqual("enabled", reasoning["thinking.type"]!.ToString());
        Assert.AreEqual(2048, reasoning["thinking.budget_tokens"]!.GetValue<int>());
        Assert.AreEqual(10, reasoning["usage.output_tokens_details.reasoning_tokens"]!.GetValue<int>());

        var second = capture.Sending(request, "{}"u8);
        const string secondBody = "data: {\"type\":\"message_start\",\"message\":{\"thinking\":{\"type\":\"disabled\"}}}\n\n";
        using var secondResponse = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(secondBody, Encoding.UTF8, "text/event-stream") };
        capture.Received(second, secondResponse);
        await ReadFragmented(secondResponse);
        await firstStream.CopyToAsync(Stream.Null);
        capture.Complete("completed", 200);
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        var current = detail["reportedReasoning"]!.AsObject();
        Assert.AreEqual(1, current.Count);
        Assert.AreEqual("disabled", current["thinking.type"]!.ToString());
        var previous = detail["attempts"]![0]!["reportedReasoning"]!;
        Assert.AreEqual("enabled", previous["thinking.type"]!.ToString());
        Assert.AreEqual(2048, previous["thinking.budget_tokens"]!.GetValue<int>());
        Assert.AreEqual("high", previous["thinking.effort"]!.ToString());
        Assert.AreEqual(11, previous["usage.output_tokens_details.reasoning_tokens"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task RealWebSocketResponseModelIsCapturedFromConvertedEvents()
    {
        const string created = """{"type":"response.created","response":{"model":"ws-upstream"}}""";
        const string done = """{"type":"response.completed","response":{"status":"completed","output":[]}}""";
        const string payload = """{"type":"response.create","model":"ws-sent","input":"question"}""";
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            Assert.AreEqual(payload, await WebSocketLoopback.ReceiveAsync(socket, ct));
            await WebSocketLoopback.SendAsync(socket, created, ct, fragmentSize: 1);
            await WebSocketLoopback.SendAsync(socket, done, ct);
        });
        using var store = new RequestLogStore();
        var capture = Begin(store, "{\"model\":\"ws-local\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Address);
        UpstreamWebSocket.PrepareHandshake(request);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var attempt = capture.Sending(request, bytes, "websocket");
        using var client = server.CreateClient();
        using var response = await UpstreamWebSocket.SendAsync(client, request, bytes, capture, attempt, CancellationToken.None);
        capture.Received(attempt, response);
        Assert.AreEqual($"event: response.created\ndata: {created}\n\nevent: response.completed\ndata: {done}\n\n", await ReadFragmented(response));
        capture.Complete("completed", 200);
        await server.Completion;
        await store.FlushAsync();
        var detail = store.Detail(capture.Id)!;
        Assert.AreEqual("ws-local", detail["requestedModel"]!.ToString());
        Assert.AreEqual("ws-sent", detail["sentModel"]!.ToString());
        Assert.AreEqual("ws-upstream", detail["reportedModel"]!.ToString());
        Assert.AreEqual(101, detail["attempts"]![0]!["status"]!.GetValue<int>());
        Assert.AreEqual("", await ReadPart(store, capture.Id, "attempt-1-websocket-events"));
        AssertNoUpstreamBodies(store, capture.Id);
    }
}
