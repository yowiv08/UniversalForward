using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ResponseAnomalyTests
{
    private const string Limit = "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"token rate limit\"}}}\n\n";
    private const string Done = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]}}\n\n";
    private const string EmptyDone = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";

    [TestMethod]
    [DataRow("data: {\"type\":\"response.output_text.delta\",\"delta\":\"rate limit exceeded\"}\n\ndata: [DONE]\n\n", false, true, true)]
    [DataRow(Limit, true, false, true)]
    [DataRow("\uFEFF" + Limit, true, false, true)]
    [DataRow("data: {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"busy\"}}\n\n", true, false, true)]
    [DataRow("data: {\"type\":\"message_start\",\"message\":{\"content\":[],\"usage\":{\"input_tokens\":10}}}\n\n", false, false, false)]
    [DataRow("data: {\"type\":\"content_block_delta\",\"delta\":{\"thinking\":\"thinking\"}}\n\n", false, true, false)]
    [DataRow("data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"id\"}}\n\n", false, true, false)]
    [DataRow("data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"reasoning\",\"summary\":[]}}\n\n", false, false, false)]
    [DataRow("data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"thinking\"}\n\n", false, true, false)]
    [DataRow("data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"encrypted\"}}\n\n", false, true, false)]
    [DataRow("data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"thinking\"}]}}\n\n", false, true, false)]
    [DataRow("data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"custom_tool_call\",\"id\":\"id\"}}\n\n", false, true, false)]
    [DataRow("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"id\"}]},\"finish_reason\":null}]}\n\n", false, true, false)]
    [DataRow("data: {\"choices\":[{\"text\":\"text\",\"finish_reason\":\"stop\"}]}\n\n", false, true, true)]
    [DataRow(Done, false, true, true)]
    [DataRow(EmptyDone, false, false, true)]
    [DataRow("event: error\ndata: {\"message\":\"token rate limit\"}\n\n", true, false, true)]
    [DataRow("data: {\"type\":\"response.completed\",\ndata: \"response\":{\"status\":\"completed\",\"output\":[]}}\r\n\r\n", false, false, true)]
    public void SeparatesOutputUsageAndOutcome(string data, bool limited, bool output, bool terminal)
    {
        var result = ForwardResponseAnalysis.Read(Encoding.UTF8.GetBytes(data), true, false);
        Assert.AreEqual(limited, result.RateLimited);
        Assert.AreEqual(output, result.HasOutput);
        Assert.AreEqual(terminal, result.Terminal);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(10)]
    public async Task ExhaustionPreservesRawUpstreamErrorWithoutOrdinaryRetry(int max)
    {
        var calls = 0;
        var client = Client(_ =>
        {
            calls++;
            return Reply(Limit);
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var policy = Policy(max);
        policy.MaxRetries = 10;
        policy.RetryStatusCodes = "200,429";
        policy.StatusCodeMapping[200] = 502;
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy));
        Assert.AreEqual(max + 1, calls);
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual("text/event-stream; charset=utf-8", result.Response.ContentType);
        Assert.AreEqual(Limit, Encoding.UTF8.GetString(result.Response.RawContent!));
    }

    [TestMethod]
    public async Task MixedAnomaliesShareBudgetAndPreserveRequest()
    {
        var bodies = new List<string>();
        var identities = new List<string>();
        var client = Client(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            identities.Add(string.Join("|", request.Headers.Select(x => x.Key + ":" + string.Join(",", x.Value))));
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(bodies.Count == 1 ? Limit : bodies.Count == 2 ? "" : Done, Encoding.UTF8, "text/event-stream") };
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var policy = Policy(2);
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", false, policy: policy));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(3, bodies.Count);
        Assert.AreEqual(1, bodies.Distinct().Count());
        Assert.AreEqual(1, identities.Distinct().Count());
        Assert.AreEqual("application/json", result.Response.ContentType);
    }

    [TestMethod]
    [DataRow("", 2, "empty_response")]
    [DataRow("data: {\"type\":\"response.created\",\"response\":{}}\n\n", 2, "missing_terminal")]
    public async Task EmptyOnlyRetriesBeforeOutput(string body, int callsExpected, string code)
    {
        var calls = 0;
        var client = Client(_ => { calls++; return Reply(body); });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(1)));
        Assert.AreEqual(callsExpected, calls);
        Assert.AreEqual(502, result.Response.StatusCode);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Response.RawContent!), code);
    }

    [TestMethod]
    [DataRow("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")]
    [DataRow("data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\"}}\n\n")]
    public async Task MissingTerminalPreservesOutputAndFailsWithoutReplay(string body)
    {
        var client = Client(_ => Reply(body));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(1)));
        Assert.AreEqual(200, result.Response.StatusCode);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var chunk in result.Response.RawStream!) await output.WriteAsync(chunk);
        });
        Assert.AreEqual(body, Encoding.UTF8.GetString(output.ToArray()));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task LongRetryAfterReturnsImmediatelyAndKeeps429()
    {
        var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\"}}") };
            response.Headers.Add("Retry-After", "600");
            return Task.FromResult(response);
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(10)));
        Assert.AreEqual(429, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public void DelayHonorsHintsAndIgnoresInvalidValues()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Retry-After", "invalid");
        response.Headers.TryAddWithoutValidation("retry-after-ms", "7000");
        Assert.AreEqual(TimeSpan.FromSeconds(7), UniversalForwardTerminal.ResponseRetryDelay(response, 1, true));
        Assert.AreEqual(TimeSpan.FromSeconds(1), UniversalForwardTerminal.ResponseRetryDelay(response, 1, false));
    }

    [TestMethod]
    public async Task PermanentQuotaDoesNotRetry429()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("{\"error\":{\"code\":\"insufficient_quota\"}}") }));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var policy = Policy(3); policy.MaxRetries = 3;
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", false, policy: policy));
        Assert.AreEqual(429, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task ConcurrentRequestsHaveIndependentCounters()
    {
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var client = Client(request =>
        {
            var session = request.Headers.GetValues("Session-Id").Single();
            return Reply(counts.AddOrUpdate(session, 1, (_, n) => n + 1) == 1 ? Limit : Done);
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", false, policy: Policy(1)))));
        Assert.IsTrue(results.All(x => x.Response.StatusCode == 200 && x.Response.ContentType == "application/json"));
        Assert.AreEqual(3, counts.Count);
        Assert.IsTrue(counts.Values.All(x => x == 2));
    }

    [TestMethod]
    public async Task SharedBudgetStopsAlternatingFailures()
    {
        var calls = 0;
        var client = Client(_ => Reply(++calls == 1 ? Limit : ""));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var policy = Policy(1); policy.MaxRetries = 10;
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy));
        Assert.AreEqual(502, result.Response.StatusCode);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task OrdinaryBudgetDoesNotResetAfterAnomaly()
    {
        var calls = 0;
        var client = Client(_ =>
        {
            calls++;
            return calls == 2 ? Reply(Limit) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{\"error\":{\"message\":\"unavailable\"}}") });
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var policy = Policy(2); policy.MaxRetries = 1;
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy));
        Assert.AreEqual(503, result.Response.StatusCode);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task FirstOutputEscapesBeforeEntireBodyIsRead()
    {
        const string first = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n";
        using var body = new ControlledStream(first, wait: true);
        var client = StreamClient(body);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var task = terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(0)));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(body.Waiting.Task.IsCompleted);
        Assert.IsFalse(body.Disposed);
        await using var output = result.Response.RawStream!.GetAsyncEnumerator();
        Assert.IsTrue(await output.MoveNextAsync());
        Assert.AreEqual(first, Encoding.UTF8.GetString(output.Current.Span));
        var next = output.MoveNextAsync().AsTask();
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(next.IsCompleted);
        body.Release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(async () => await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(body.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedBodyOnlyRetriesWithoutOutput(bool hasOutput)
    {
        var calls = 0;
        var streams = new List<ControlledStream>();
        var client = Client(_ =>
        {
            calls++;
            var body = new ControlledStream(hasOutput
                ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"
                : "data: {\"type\":\"response.created\",\"response\":{}}\n\n", fail: true);
            streams.Add(body);
            return StreamReply(body);
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(1)));
        Assert.AreEqual(hasOutput ? 200 : 502, result.Response.StatusCode);
        if (hasOutput)
        {
            using var output = new MemoryStream();
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                await foreach (var chunk in result.Response.RawStream!) await output.WriteAsync(chunk);
            });
            StringAssert.Contains(Encoding.UTF8.GetString(output.ToArray()), "partial");
        }
        Assert.AreEqual(hasOutput ? 1 : 2, calls);
        Assert.IsTrue(streams.All(x => x.Disposed));
    }

    [TestMethod]
    public async Task CancellationStopsBufferedReadAndDisposesResponse()
    {
        using var cts = new CancellationTokenSource();
        using var body = new ControlledStream("", wait: true);
        var client = StreamClient(body);
        var context = ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(3), cancellation: cts.Token);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var task = terminal.InvokeAsync(context);
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.IsTrue(body.Disposed);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task TotalTimeoutReturnsPreviousCompleteRateLimit()
    {
        var calls = 0;
        using var body = new ControlledStream("", wait: true);
        var client = Client(_ => ++calls == 1 ? Reply(Limit) : StreamReply(body));
        var policy = Policy(3); policy.TotalTimeoutSeconds = 2;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(Limit, Encoding.UTF8.GetString(result.Response.RawContent!));
        Assert.IsTrue(body.Disposed);
    }

    [TestMethod]
    public async Task BufferOverflowIsNotRetried()
    {
        using var body = new MemoryStream(new byte[32 * 1024 * 1024 + 1]);
        var client = StreamClient(body);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(3)));
        Assert.AreEqual(502, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Response.RawContent!), "32 MiB");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MalformedPayloadIsNotRetried(bool invalidUtf8)
    {
        using var body = new MemoryStream(invalidUtf8 ? new byte[] { 0xff } : Encoding.UTF8.GetBytes("data: {broken}\n\n"));
        var client = StreamClient(body);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(3)));
        Assert.AreEqual(502, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task TotalTimeoutWithoutCompleteRateLimitReturns504()
    {
        using var body = new ControlledStream("", wait: true);
        var client = StreamClient(body);
        var policy = Policy(3); policy.TotalTimeoutSeconds = 1;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy));
        Assert.AreEqual(504, result.Response.StatusCode);
        Assert.IsTrue(body.Disposed);
    }

    [TestMethod]
    public async Task IdleTimeoutCanRetryBeforeAnyOutput()
    {
        using var body = new ControlledStream("", wait: true);
        var calls = 0;
        var client = Client(_ => ++calls == 1 ? StreamReply(body) : Reply(Done));
        var policy = Policy(1); policy.StreamIdleTimeoutSeconds = 1;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", false, policy: policy));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(2, calls);
        Assert.IsTrue(body.Disposed);
    }

    [TestMethod]
    public async Task CancellationDuringDelayDoesNotStartAnotherAttempt()
    {
        using var cts = new CancellationTokenSource();
        var client = Client(_ => Reply(Limit));
        var policy = Policy(3); policy.ResponseRetryIntervalSeconds = 10;
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var task = terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: policy, cancellation: cts.Token));
        cts.CancelAfter(100);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("data: {\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n")]
    [DataRow("data: {\"type\":\"error\",\"error\":{\"message\":\"other error\"}}\n\n")]
    [DataRow(Done)]
    public async Task ExplicitOutcomesNeverBecomeEmptyRetries(string body)
    {
        var client = Client(_ => Reply(body));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(3)));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
        if (result.Response.RawStream is { } stream) await foreach (var _ in stream) { }
    }

    [TestMethod]
    [DataRow(false, false, -1)]
    [DataRow(false, false, 0)]
    [DataRow(false, false, 10)]
    [DataRow(false, true, -1)]
    [DataRow(false, true, 0)]
    [DataRow(false, true, 10)]
    [DataRow(true, false, -1)]
    [DataRow(true, false, 0)]
    [DataRow(true, false, 10)]
    [DataRow(true, true, -1)]
    [DataRow(true, true, 0)]
    [DataRow(true, true, 10)]
    public async Task CompletedOutputRatherThanReasoningTokenCountControlsEmptyRetry(bool sse, bool hasText, int reasoningTokens)
    {
        var calls = 0;
        string Response(bool text)
        {
            var output = text ? "[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]" : "[]";
            var details = reasoningTokens < 0 ? "" : $",\"output_tokens_details\":{{\"reasoning_tokens\":{reasoningTokens}}}";
            var response = $"{{\"status\":\"completed\",\"output\":{output},\"usage\":{{\"input_tokens\":3,\"output_tokens\":2{details}}}}}";
            return sse ? $"data: {{\"type\":\"response.completed\",\"response\":{response}}}\n\n" : response;
        }
        var client = Client(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Response(hasText || calls > 1), Encoding.UTF8, sse ? "text/event-stream" : "application/json") });
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", sse, policy: Policy(1)));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(hasText ? 1 : 2, calls);
        using var body = new MemoryStream();
        if (result.Response.RawStream is { } stream)
            await foreach (var chunk in stream) await body.WriteAsync(chunk);
        else await body.WriteAsync(result.Response.RawContent!);
        Assert.AreEqual(Response(true), Encoding.UTF8.GetString(body.ToArray()));
    }

    [TestMethod]
    [DataRow(false, "response.completed")]
    [DataRow(false, "response.done")]
    [DataRow(true, "response.completed")]
    [DataRow(true, "response.done")]
    public async Task CompletedEmptyResponsesWithoutUsageExhaustExistingBudget(bool sse, string type)
    {
        var calls = 0;
        var response = "{\"status\":\"completed\",\"output\":[]}";
        var payload = sse ? $"data: {{\"type\":\"{type}\",\"response\":{response}}}\n\n" : response;
        var client = Client(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, sse ? "text/event-stream" : "application/json") });
        });
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var policy = Policy(1);
        policy.MaxRetries = 10;
        policy.RetryStatusCodes = "200";
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", sse, policy: policy));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(502, result.Response.StatusCode);
        Assert.AreEqual(PluginRetryAction.NextAttempt, result.Attempt.Decision!.Retry);
        Assert.AreEqual("empty_response", result.Attempt.Decision.ReasonCode);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Response.RawContent!), "empty_response");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CompletedEmptyResponsesPassThroughWhenRetryIsDisabled(bool sse, bool stream)
    {
        const string response = "{\"status\":\"completed\",\"output\":[]}";
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse ? EmptyDone : response, Encoding.UTF8, sse ? "text/event-stream" : "application/json") }));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var policy = Policy(3);
        policy.EmptyResponseRetryEnabled = false;
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", stream, policy: policy));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
        using var body = new MemoryStream();
        if (result.Response.RawStream is { } chunks)
            await foreach (var chunk in chunks) await body.WriteAsync(chunk);
        else await body.WriteAsync(result.Response.RawContent!);
        Assert.AreEqual(stream ? EmptyDone : response, Encoding.UTF8.GetString(body.ToArray()));
    }

    [TestMethod]
    [DataRow("data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"call_id\":\"call\",\"name\":\"lookup\",\"arguments\":\"{}\"}}\n\n")]
    [DataRow("data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"thinking\"}\n\n")]
    [DataRow("data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"encrypted\"}}\n\n")]
    public async Task CompletedEmptyTerminalNeverReplaysPreviouslyEmittedOutput(string prefix)
    {
        var payload = prefix + EmptyDone;
        var client = Client(_ => Reply(payload));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(3)));
        Assert.AreEqual(200, result.Response.StatusCode);
        using var body = new MemoryStream();
        await foreach (var chunk in result.Response.RawStream!) await body.WriteAsync(chunk);
        Assert.AreEqual(payload, Encoding.UTF8.GetString(body.ToArray()));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("data: [DONE]\n\n")]
    [DataRow("data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"reasoning\",\"status\":\"completed\",\"summary\":[]}}\n\ndata: [DONE]\n\n")]
    public async Task DoneMarkerAndCompletedItemDoNotBecomeCompletedEmptyResponses(string payload)
    {
        var analysis = ForwardResponseAnalysis.Read(Encoding.UTF8.GetBytes(payload), true, false);
        Assert.IsFalse(analysis.CompletedResponses);
        var client = Client(_ => Reply(payload));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true, policy: Policy(3)));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(1, client.Invocations.Count);
        using var body = new MemoryStream();
        await foreach (var chunk in result.Response.RawStream!) await body.WriteAsync(chunk);
        Assert.AreEqual(payload, Encoding.UTF8.GetString(body.ToArray()));
    }

    [TestMethod]
    public void DefaultPolicyAndValidation()
    {
        var policy = System.Text.Json.JsonSerializer.Deserialize<ForwardRequestPolicy>("{}")!;
        Assert.IsFalse(policy.RateLimitRetryEnabled);
        Assert.IsFalse(policy.EmptyResponseRetryEnabled);
        Assert.AreEqual(3, policy.ResponseMaxRetries);
        Assert.AreEqual(5, policy.ResponseRetryIntervalSeconds);
        policy.ResponseMaxRetries = 11;
        Assert.ThrowsExactly<FormatException>(policy.Validate);
        policy.ResponseMaxRetries = 0;
        policy.ResponseRetryIntervalSeconds = 0;
        Assert.ThrowsExactly<FormatException>(policy.Validate);
    }

    private static Mock<IPluginHttpClient> StreamClient(Stream stream) => Client(_ => StreamReply(stream));
    private static Task<HttpResponseMessage> StreamReply(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("text/event-stream");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class ControlledStream(string prefix, bool wait = false, bool fail = false) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _prefix.ReadAsync(buffer, cancellationToken);
            if (n != 0) return n;
            Waiting.TrySetResult();
            if (wait) await Release.Task.WaitAsync(cancellationToken);
            if (fail) throw new IOException("connection closed");
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; _prefix.Dispose(); base.Dispose(disposing); }
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

    private static ForwardRequestPolicy Policy(int max) => new()
    { RateLimitRetryEnabled = true, EmptyResponseRetryEnabled = true, ResponseMaxRetries = max, ResponseRetryIntervalSeconds = 1 };

    private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });

    private static Mock<IPluginHttpClient> Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
    {
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) => reply(request));
        return client;
    }
}
