using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ResponseErrorEvidenceTests
{
    internal const string TokenLimit = """
        event: error
        data: {"type":"error","error":{"type":"too_many_requests","code":"rate_limit_exceeded","headers":{"x-ms-fe-error":"true"},"message":"Your requests to gpt-6-astra for gpt-6-astra in eastus2 have exceeded token rate limit.","param":null},"sequence_number":1}


        """;
    internal const string ThroughputLimit = """
        event: error
        data: {"type":"error","error":{"type":"too_many_requests","code":"rate_limit_reached","headers":{"skip-error-remapping":"true"},"message":"Requests have exceeded the throughput limit on your Provisioned-Managed deployment. If you continue to exceed your limit, consider increasing the number of provisioned throughput units deployed.","param":null},"sequence_number":2}


        """;

    [TestMethod]
    [DataRow(TokenLimit, true, false)]
    [DataRow(TokenLimit, true, true)]
    [DataRow(TokenLimit, false, false)]
    [DataRow(TokenLimit, false, true)]
    [DataRow(ThroughputLimit, true, false)]
    [DataRow(ThroughputLimit, true, true)]
    public async Task Http200RateLimitIsAJsonFailureEvenWithoutRetry(string frame, bool stream, bool retriesEnabled)
    {
        var client = Client(frame);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", stream,
            policy: new ForwardRequestPolicy { RateLimitRetryEnabled = retriesEnabled, ResponseMaxRetries = 0 }));
        Assert.AreEqual(429, result.Response.StatusCode);
        Assert.IsFalse(result.Response.IsStreaming);
        Assert.AreEqual("application/json", result.Response.ContentType);
        using var body = JsonDocument.Parse(result.Response.RawContent!);
        var error = body.RootElement.GetProperty("error");
        Assert.AreEqual("too_many_requests", error.GetProperty("type").GetString());
        Assert.AreEqual(frame == TokenLimit ? "rate_limit_exceeded" : "rate_limit_reached", error.GetProperty("code").GetString());
        Assert.IsTrue(error.TryGetProperty("headers", out _));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public void CompletedToolItemDoesNotCompleteTheResponse()
    {
        var analysis = ForwardResponseAnalysis.Read(Encoding.UTF8.GetBytes(
            "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\",\"status\":\"completed\",\"arguments\":\"{}\"}}\n\n"),
            true, false);
        Assert.IsTrue(analysis.HasOutput);
        Assert.IsFalse(analysis.Terminal);
    }

    [TestMethod]
    public async Task MissingResponseTerminalFailsAfterPreservingToolOutputWithoutReplay()
    {
        const string frame = "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\",\"status\":\"completed\",\"arguments\":\"{}\"}}\n\n";
        var client = Client(frame);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true,
            policy: new ForwardRequestPolicy { RateLimitRetryEnabled = true, EmptyResponseRetryEnabled = true }));
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var chunk in result.Response.RawStream!) output.Write(chunk.Span);
        });
        StringAssert.Contains(error.Message, "missing_terminal");
        Assert.AreEqual(frame, Encoding.UTF8.GetString(output.ToArray()));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("application/json")]
    [DataRow("text/event-stream")]
    public async Task JsonErrorWithHttp200DoesNotMasqueradeAsSuccessfulStream(string contentType)
    {
        var client = Client("""{"error":{"type":"too_many_requests","message":"busy"}}""", contentType);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "claude", true));
        Assert.AreEqual(429, result.Response.StatusCode);
        Assert.AreEqual("application/json", result.Response.ContentType);
        Assert.IsNull(result.Response.RawStream);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow("invalid_request", "bad argument", 502)]
    [DataRow("insufficient_quota", "quota exhausted", 502)]
    [DataRow("rate_limit_exceeded", "token rate limit", 429)]
    public async Task ErrorDetailsAreLoggedWithoutCredentialAndCannotMapBackToSuccess(string code, string message, int status)
    {
        var frame = "event: error\ndata: " + JsonSerializer.Serialize(new
        {
            type = "error", error = new { code, message = message + " channel-secret" }
        }) + "\n\n";
        var client = Client(frame);
        var host = PluginTestHost.Create("universalforward");
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", true,
            policy: new ForwardRequestPolicy
            {
                MaxRetries = 3, RetryStatusCodes = "200,429", RateLimitRetryEnabled = true, ResponseMaxRetries = 0,
                StatusCodeMapping = new() { [200] = 201, [429] = 200, [502] = 200 }
            }));
        Assert.AreEqual(status, result.Response.StatusCode);
        var log = Mock.Get(host.Services.Log).Invocations.Select(x => x.Arguments[0]).OfType<PluginLog>()
            .Single(x => x.EventType == "request.response.failed");
        StringAssert.Contains(log.Message, message);
        StringAssert.Contains(log.DetailsJson!, code);
        Assert.IsFalse(log.Message.Contains("channel-secret", StringComparison.Ordinal));
        Assert.IsFalse(Encoding.UTF8.GetString(result.Response.RawContent!).Contains("channel-secret", StringComparison.Ordinal));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task NonstreamIncompleteResponseKeepsGeneratedOutputWithoutRetry()
    {
        const string frame = """
            data: {"type":"response.incomplete","response":{"id":"resp","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"message","content":[{"type":"output_text","text":"partial"}]}]}}


            """;
        var client = Client(frame);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(ForwardResponseHandlingTests.Context(client.Object, "codex", false,
            policy: new ForwardRequestPolicy { RateLimitRetryEnabled = true, EmptyResponseRetryEnabled = true }));
        Assert.AreEqual(200, result.Response.StatusCode);
        using var body = JsonDocument.Parse(result.Response.RawContent!);
        Assert.AreEqual("incomplete", body.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("partial", body.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public void OutputContentCannotSupplyAnUpstreamErrorOrTerminalStatus()
    {
        var result = ForwardResponseAnalysis.Read(Encoding.UTF8.GetBytes("""
            data: {"type":"response.output_item.done","item":{"type":"message","status":"completed","error":{"code":"rate_limit_exceeded"},"content":[{"type":"output_text","text":"rate limit exceeded"}]}}


            """), true, false);
        Assert.IsTrue(result.HasOutput);
        Assert.IsFalse(result.HasError);
        Assert.IsFalse(result.RateLimited);
        Assert.IsFalse(result.Terminal);
    }

    [TestMethod]
    public void NestedErrorKeepsTheUpstreamDetailsInsteadOfTheWrapperMessage()
    {
        var result = ForwardResponseAnalysis.Read(Encoding.UTF8.GetBytes("""
            data: {"type":"response.failed","message":"wrapper","response":{"status":"failed","error":{"code":"rate_limit_exceeded","message":"token rate limit"}}}


            """), true, false);
        Assert.AreEqual("rate_limit_exceeded", result.ErrorCode);
        Assert.AreEqual("token rate limit", result.ErrorMessage);
        Assert.IsTrue(result.RateLimited);
    }

    private static Mock<IPluginHttpClient> Client(string text, string contentType = "text/event-stream")
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(text, Encoding.UTF8, contentType) });
        return client;
    }
}
