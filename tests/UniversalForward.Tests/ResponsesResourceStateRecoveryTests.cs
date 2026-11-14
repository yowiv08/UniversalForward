using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

public sealed partial class ResponsesResourceStateForwardingTests
{
    private const string StateErrorEvent = """{"type":"error","error":{"code":"invalid_encrypted_content","message":"The encrypted content could not be verified."}}""";

    [TestMethod]
    public async Task NextIndependentRequestPreservesHistoryAfterOneRecovery()
    {
        var payloads = new List<JsonObject>();
        var client = Client(async (request, ct) =>
        {
            payloads.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            return new HttpResponseMessage(payloads.Count == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(payloads.Count == 1 ? InvalidState : ResponseBody, Encoding.UTF8, "application/json") };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true, codex: true);
        var host = PluginTestHost.Create("universalforward");
        using var terminal = new UniversalForwardTerminal(host);
        var original = History();
        var before = original.ToJsonString();

        for (var index = 0; index < 2; index++)
        {
            var context = Context(account, client.Object, original);
            Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
            Assert.AreEqual(before, context.Request.OriginalBody!.Value.GetRawText());
        }

        Assert.AreEqual(3, payloads.Count);
        AssertHistory(payloads[0], stripped: false);
        AssertHistory(payloads[1], stripped: true);
        AssertHistory(payloads[2], stripped: false);
        Assert.AreEqual(payloads[0]["client_metadata"]!["session_id"]!.ToString(),
            payloads[1]["client_metadata"]!["session_id"]!.ToString());
        var log = Mock.Get(host.Services.Log).Invocations.Select(call => call.Arguments[0]).OfType<PluginLog>()
            .Single(log => log.EventType == "request.responses.state.stripped");
        var details = JsonNode.Parse(log.DetailsJson!)!;
        Assert.AreEqual("invalid_encrypted_content", details["reason"]!.ToString());
        Assert.AreEqual(3, details["removedReasoningItems"]!.GetValue<int>());
        Assert.IsFalse(log.DetailsJson!.Contains("old ciphertext", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(400, """{"error":{"message":"bad response status code 400","type":"invalid_request_error","param":"input","code":null}}""")]
    [DataRow(400, """{"error":{"message":"Item 'fc_old' was provided without its required 'reasoning' item."}}""")]
    [DataRow(400, """{"error":{"param":"reasoning.effort","message":"Unsupported value: reasoning.effort"}}""")]
    [DataRow(400, """{"error":{"code":"context_length_exceeded","message":"Context is too long."}}""")]
    [DataRow(401, """{"error":{"code":"invalid_api_key","message":"Incorrect API key"}}""")]
    [DataRow(404, """{"error":{"code":"model_not_found","message":"The model does not exist."}}""")]
    [DataRow(429, """{"error":{"code":"rate_limit_exceeded","message":"Too many requests"}}""")]
    [DataRow(503, """{"error":{"code":"server_error","message":"Service unavailable"}}""")]
    [DataRow(400, "{ malformed JSON")]
    [DataRow(400, "bad response status code 400")]
    public async Task UnrelatedErrorsKeepHistoryAndOriginalError(int status, string error)
    {
        var client = Client(async (request, ct) =>
        {
            AssertHistory(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(), stripped: false);
            return new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(error, Encoding.UTF8, "application/json") };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, History()));

        Assert.AreEqual(status, result.Response.StatusCode);
        Assert.AreEqual(error, Encoding.UTF8.GetString(result.Response.RawContent!));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedStateErrorStopsAfterOneRecoveryBeforeStatusMapping(bool mapped)
    {
        var sends = 0;
        var client = Client(async (request, ct) =>
        {
            AssertHistory(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(), stripped: sends++ > 0);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent(InvalidState, Encoding.UTF8, "application/json") };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true, retries: 10);
        EndpointHeaderOverrideTests.Edit(account, settings =>
        {
            settings["requestPolicy"]!["retryStatusCodes"] = "400";
            if (mapped) settings["requestPolicy"]!["statusCodeMapping"] = new JsonObject { ["400"] = 200 };
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, History()));

        Assert.AreEqual(mapped ? 200 : 400, result.Response.StatusCode);
        Assert.AreEqual(InvalidState, Encoding.UTF8.GetString(result.Response.RawContent!));
        Assert.AreEqual(2, sends);
        Assert.AreEqual(Router.Contracts.Domain.PluginRetryAction.None, result.Attempt.Decision!.Retry);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Http200SseStateErrorRecoversBeforeOutput(bool codex, bool stream)
    {
        var sends = 0;
        var client = Client(async (request, ct) =>
        {
            AssertHistory(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(), stripped: sends++ > 0);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(sends == 1 ? "event: error\ndata: " + StateErrorEvent + "\n\n" : Frame(Completed), Encoding.UTF8, "text/event-stream") };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true, codex: codex);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, History(stream), stream: stream));

        Assert.AreEqual(200, result.Response.StatusCode);
        if (result.Response.IsStreaming) Assert.AreEqual(Frame(Completed), await ReadAll(result.Response.RawStream!));
        else Assert.AreEqual("resp_upstream", JsonNode.Parse(result.Response.RawContent!)!["id"]!.ToString());
        Assert.AreEqual(2, sends);
    }

    [TestMethod]
    public async Task Http200JsonStateErrorAlsoRecovers()
    {
        var sends = 0;
        var client = Client(async (request, ct) =>
        {
            AssertHistory(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(), stripped: sends++ > 0);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(sends == 1 ? InvalidState : ResponseBody, Encoding.UTF8, "application/json") };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, History()));

        Assert.AreEqual(ResponseBody, Encoding.UTF8.GetString(result.Response.RawContent!));
        Assert.AreEqual(2, sends);
    }

    [TestMethod]
    [DataRow("""{"type":"response.output_text.delta","delta":"partial answer"}""")]
    [DataRow("""{"type":"response.reasoning_summary_text.delta","delta":"thinking"}""")]
    [DataRow("""{"type":"response.output_item.done","item":{"type":"reasoning","encrypted_content":"new state"}}""")]
    [DataRow("""{"type":"response.output_item.added","item":{"type":"function_call","call_id":"call","arguments":""}}""")]
    public async Task StateErrorAfterOutputNeverStripsOrReplays(string output)
    {
        var frame = "data: " + output + "\n\nevent: error\ndata: " + StateErrorEvent + "\n\n";
        var client = Client(async (request, ct) =>
        {
            AssertHistory(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(), stripped: false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(frame, Encoding.UTF8, "text/event-stream") };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true, codex: true, retries: 2);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, History(stream: true), stream: true));

        using var received = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var chunk in result.Response.RawStream!) received.Write(chunk.Span);
        });
        Assert.AreEqual(frame, Encoding.UTF8.GetString(received.ToArray()));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("compaction")]
    [DataRow("item_reference")]
    public async Task OpaqueHistoryFailurePreservesOriginalUpstreamError(string type)
    {
        var original = History();
        original["input"]!.AsArray().Add(new JsonObject { ["type"] = type, ["id"] = "opaque-id", ["encrypted_content"] = "opaque-state" });
        var client = Client(async (request, ct) =>
        {
            var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            AssertHistory(sent, stripped: false, opaque: true);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent(InvalidState, Encoding.UTF8, "application/json") };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true);
        var host = PluginTestHost.Create("universalforward");
        using var terminal = new UniversalForwardTerminal(host);

        var result = await terminal.InvokeAsync(Context(account, client.Object, original));

        Assert.AreEqual(400, result.Response.StatusCode);
        Assert.AreEqual(InvalidState, Encoding.UTF8.GetString(result.Response.RawContent!));
        Assert.AreEqual(1, client.Invocations.Count);
        var log = Mock.Get(host.Services.Log).Invocations.Select(call => call.Arguments[0]).OfType<PluginLog>()
            .Single(log => log.EventType == "request.responses.state.skipped");
        StringAssert.Contains(log.DetailsJson!, "opaque_history");
    }

    [TestMethod]
    public async Task ErrorWithoutAnyResourceStateDoesNotResendUnchangedRequest()
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent(InvalidState, Encoding.UTF8, "application/json") }));
        var account = Account(new Uri("https://upstream.example"), enabled: true);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, new JsonObject { ["input"] = "question" }));

        Assert.AreEqual(400, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task WebSocketStateErrorRebuildsPayloadAndRetriesOnce(bool codex, bool stream)
    {
        var payloads = new List<JsonObject>();
        var auth = new List<string>();
        await using var server = new WebSocketLoopback(async (peer, index, ct) =>
        {
            auth.Add(peer.Headers["Authorization"]);
            using var socket = await peer.AcceptAsync(ct);
            payloads.Add(JsonNode.Parse(await WebSocketLoopback.ReceiveAsync(socket, ct))!.AsObject());
            await WebSocketLoopback.SendAsync(socket, index == 0 ? StateErrorEvent : Completed, ct);
        }, connections: 2);
        var account = Account(server.Address, enabled: true, codex: codex, websocket: true);
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http).Setup(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => server.CreateClient());
        using var terminal = new UniversalForwardTerminal(host);

        var result = await terminal.InvokeAsync(Context(account, new Mock<IPluginHttpClient>(MockBehavior.Strict).Object,
            History(stream), stream: stream));

        Assert.AreEqual(200, result.Response.StatusCode);
        if (stream) Assert.AreEqual(Frame(Completed), await ReadAll(result.Response.RawStream!));
        else Assert.AreEqual("resp_upstream", JsonNode.Parse(result.Response.RawContent!)!["id"]!.ToString());
        await server.Completion;
        Assert.AreEqual(2, server.Requests);
        AssertHistory(payloads[0], stripped: false);
        AssertHistory(payloads[1], stripped: true);
        Assert.AreEqual(auth[0], auth[1]);
        foreach (var body in payloads)
        {
            Assert.AreEqual("response.create", body["type"]!.ToString());
            Assert.IsNull(body["stream"]);
        }
    }
}
