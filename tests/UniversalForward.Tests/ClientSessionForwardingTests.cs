using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ClientSessionForwardingTests
{
    private const string Done = """{"type":"response.completed","response":{"id":"response-fixture","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":2,"output_tokens":1}}}""";
    private const string Messages = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"message-fixture\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"usage\":{\"input_tokens\":2}}}\n\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
        + "data: {\"type\":\"message_stop\"}\n\n";

    [TestMethod]
    [DataRow("codex", "http", false)]
    [DataRow("codex", "http", true)]
    [DataRow("codex", "websocket", false)]
    [DataRow("codex", "websocket", true)]
    [DataRow("claude", "http", false)]
    [DataRow("claude", "http", true)]
    public async Task AnonymousTurnsReuseIdentityAcrossKeysAndTransportModes(string profile, string transport, bool stream)
    {
        var observed = new List<Captured>();
        await using var server = transport == "websocket" ? new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            observed.Add(new(peer.Headers, JsonNode.Parse(await WebSocketLoopback.ReceiveAsync(socket, ct))!.AsObject()));
            await WebSocketLoopback.SendAsync(socket, Done, ct);
        }, connections: 3) : null;
        var account = Account(profile, transport, server?.Address);
        var host = ChannelKeysTests.Host(account);
        if (server is not null)
            Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
                .Returns(() => server.CreateClient());
        var http = HttpClient(async request =>
        {
            observed.Add(await Capture(request));
            return Reply(profile);
        });
        using var terminal = new UniversalForwardTerminal(host);
        var endpoint = profile == "codex" ? "/v1/responses" : "/v1/messages";
        var first = Context(account, http.Object, endpoint, stream, "client-key", "question-private");
        var second = Context(account, http.Object, endpoint, stream, "client-key", "question-private", "answer", "next");
        first.Request.DownstreamRequestHeaders["User-Agent"] = ["first-client"];
        second.Request.DownstreamRequestHeaders["User-Agent"] = ["updated-client"];
        var other = Context(account, http.Object, endpoint, stream, "other-client-key", "question-private", "answer", "next");
        await Complete(terminal, first, stream);
        await Complete(terminal, second, stream);
        await Complete(terminal, other, stream);
        if (server is not null) await server.Completion;
        Assert.AreEqual(3, observed.Count);
        var sessionHeader = profile == "codex" ? "Session-Id" : "x-claude-code-session-id";
        Assert.AreEqual(observed[0].Headers[sessionHeader], observed[1].Headers[sessionHeader]);
        Assert.AreNotEqual(observed[1].Headers[sessionHeader], observed[2].Headers[sessionHeader]);
        Assert.AreNotEqual(observed[0].Headers["X-Request-Ref"], observed[1].Headers["X-Request-Ref"]);
        Assert.AreNotEqual(observed[0].Headers["X-Turn-Ref"], observed[1].Headers["X-Turn-Ref"]);
        var authHeader = profile == "codex" ? "Authorization" : "x-api-key";
        Assert.AreNotEqual(observed[0].Headers[authHeader], observed[1].Headers[authHeader], "Upstream Keys should have rotated.");
        Assert.AreEqual("updated-client", observed[1].Headers["User-Agent"]);
        foreach (var sent in observed)
        {
            Assert.AreEqual("keep-client-cache", sent.Body["prompt_cache_key"]!.ToString());
            if (profile == "codex")
            {
                Assert.AreEqual(sent.Headers["Session-Id"], sent.Body["client_metadata"]!["session_id"]!.ToString());
                var metadata = JsonNode.Parse(sent.Headers["X-Codex-Turn-Metadata"])!;
                Assert.AreEqual(sent.Headers["Session-Id"], metadata["session_id"]!.ToString());
                Assert.AreEqual(sent.Headers["X-Turn-Ref"], metadata["turn_id"]!.ToString());
                Assert.AreEqual(sent.Headers["X-Request-Ref"], sent.Headers["X-Client-Request-Id"]);
            }
            else
                Assert.AreEqual(sent.Headers[sessionHeader], JsonNode.Parse(sent.Body["metadata"]!["user_id"]!.ToString())!["session_id"]!.ToString());
        }
        if (profile == "codex")
        {
            var a = JsonNode.Parse(observed[0].Headers["X-Codex-Turn-Metadata"])!;
            var b = JsonNode.Parse(observed[1].Headers["X-Codex-Turn-Metadata"])!;
            Assert.AreEqual(a["installation_id"]!.ToString(), b["installation_id"]!.ToString());
            Assert.AreEqual(a["context_window_id"]!.ToString(), b["context_window_id"]!.ToString());
            Assert.AreEqual(observed[0].Headers["Thread-Id"], observed[1].Headers["Thread-Id"]);
            Assert.AreEqual(observed[0].Headers["X-Codex-Window-Id"], observed[1].Headers["X-Codex-Window-Id"]);
        }
        else
        {
            var a = JsonNode.Parse(observed[0].Body["metadata"]!["user_id"]!.ToString())!;
            var b = JsonNode.Parse(observed[1].Body["metadata"]!["user_id"]!.ToString())!;
            Assert.AreEqual(a["device_id"]!.ToString(), b["device_id"]!.ToString());
        }
        var logs = IdentityLogs(host);
        Assert.AreEqual(3, logs.Length);
        Assert.AreEqual("new", JsonNode.Parse(logs[0].DetailsJson!)!["source"]!.ToString());
        Assert.AreEqual("history", JsonNode.Parse(logs[1].DetailsJson!)!["source"]!.ToString());
        var diagnostics = JsonSerializer.Serialize(logs);
        foreach (var secret in new[] { "question-private", "client-key", "upstream-one", "upstream-two" })
            Assert.IsFalse(diagnostics.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetryReusesAllIdentityAndNextTurnGetsNewRequestId(bool stream)
    {
        var observed = new List<Captured>();
        var account = Account("codex", "http", retries: 1);
        var host = ChannelKeysTests.Host(account);
        var client = HttpClient(async request =>
        {
            observed.Add(await Capture(request));
            return observed.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":{"message":"retry-fixture"}}""") }
                : Reply("codex");
        });
        using var terminal = new UniversalForwardTerminal(host);
        await Complete(terminal, Context(account, client.Object, "/v1/responses", stream, "client", "question"), stream);
        await Complete(terminal, Context(account, client.Object, "/v1/responses", stream, "client", "question", "answer", "next"), stream);
        Assert.AreEqual(3, observed.Count);
        Assert.IsTrue(JsonNode.DeepEquals(observed[0].Body, observed[1].Body));
        foreach (var (name, value) in observed[0].Headers) Assert.AreEqual(value, observed[1].Headers[name], name);
        Assert.AreEqual(observed[1].Headers["Session-Id"], observed[2].Headers["Session-Id"]);
        Assert.AreNotEqual(observed[1].Headers["X-Client-Request-Id"], observed[2].Headers["X-Client-Request-Id"]);
        Assert.AreEqual(2, IdentityLogs(host).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProvidedIdentitiesKeepPriorityAndDoNotEnterHistoryCache(bool configured)
    {
        var observed = new List<Captured>();
        var account = Account("codex", "http");
        if (configured) EndpointHeaderOverrideTests.Edit(account, settings =>
        {
            settings["headerOverride"]!["Session-Id"] = "configured-session";
            settings["headerOverride"]!["X-Client-Request-Id"] = "configured-request";
        });
        var host = ChannelKeysTests.Host(account);
        var client = HttpClient(async request => { observed.Add(await Capture(request)); return Reply("codex"); });
        using var terminal = new UniversalForwardTerminal(host);
        var first = Context(account, client.Object, "/v1/responses", false, "client", "question");
        first.Request.DownstreamRequestHeaders["Session-Id"] = ["native-session"];
        first.Request.DownstreamRequestHeaders["Thread-Id"] = ["native-thread"];
        first.Request.DownstreamRequestHeaders["X-Client-Request-Id"] = ["native-request"];
        await Complete(terminal, first, false);
        var followup = Context(account, client.Object, "/v1/responses", false, "client", "question", "answer", "next");
        await Complete(terminal, followup, false);
        Assert.AreEqual(configured ? "configured-session" : "native-session", observed[0].Headers["Session-Id"]);
        Assert.AreEqual("native-thread", observed[0].Headers["Thread-Id"]);
        Assert.AreEqual(configured ? "configured-request" : "native-request", observed[0].Headers["X-Client-Request-Id"]);
        Assert.AreEqual(observed[0].Headers["Session-Id"], JsonNode.Parse(observed[0].Headers["X-Codex-Turn-Metadata"])!["session_id"]!.ToString());
        if (configured) Assert.AreEqual("configured-session", observed[1].Headers["Session-Id"]);
        else Assert.AreNotEqual(observed[0].Headers["Session-Id"], observed[1].Headers["Session-Id"]);
        var logs = IdentityLogs(host);
        Assert.AreEqual(configured ? "override" : "client", JsonNode.Parse(logs[0].DetailsJson!)!["source"]!.ToString());
        Assert.AreEqual(configured ? "override" : "new", JsonNode.Parse(logs[1].DetailsJson!)!["source"]!.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyIdentityOverridesBeatCompletionAndYieldToModernOverrides(bool modern)
    {
        var observed = new List<Captured>();
        var account = Account("codex", "http");
        EndpointHeaderOverrideTests.Edit(account, settings =>
        {
            settings["extraParams"] = """{"ReplaceHeaders":{"Session-Id":"legacy-session","X-Client-Request-Id":"legacy-request"}}""";
            if (modern)
            {
                settings["headerOverride"]!["Session-Id"] = "modern-session";
                settings["headerOverride"]!["X-Client-Request-Id"] = "modern-request";
            }
        });
        var host = ChannelKeysTests.Host(account);
        var client = HttpClient(async request => { observed.Add(await Capture(request)); return Reply("codex"); });
        using var terminal = new UniversalForwardTerminal(host);
        var first = Context(account, client.Object, "/v1/responses", false, "client", "question");
        first.Request.DownstreamRequestHeaders["Session-Id"] = ["client-session"];
        first.Request.DownstreamRequestHeaders["X-Client-Request-Id"] = ["client-request"];
        await Complete(terminal, first, false);
        var sent = observed.Single();
        var expectedSession = modern ? "modern-session" : "legacy-session";
        var expectedRequest = modern ? "modern-request" : "legacy-request";
        Assert.AreEqual(expectedSession, sent.Headers["Session-Id"]);
        Assert.AreEqual(expectedSession, sent.Headers["Thread-Id"]);
        Assert.AreEqual(expectedSession + ":0", sent.Headers["X-Codex-Window-Id"]);
        Assert.AreEqual(expectedSession, sent.Body["client_metadata"]!["session_id"]!.ToString());
        Assert.AreEqual(expectedSession, JsonNode.Parse(sent.Headers["X-Codex-Turn-Metadata"])!["session_id"]!.ToString());
        Assert.AreEqual(expectedRequest, sent.Headers["X-Client-Request-Id"]);
        Assert.AreEqual(expectedRequest, sent.Headers["X-Request-Ref"]);
        Assert.AreEqual("override", JsonNode.Parse(IdentityLogs(host).Single().DetailsJson!)!["source"]!.ToString());
    }

    private static Account Account(string profile, string transport, Uri? address = null, int retries = 0)
    {
        var account = ChannelKeysTests.Account([
            new ChannelKey { Id = "first", Secret = "upstream-one" },
            new ChannelKey { Id = "second", Secret = "upstream-two" }
        ], retries);
        EndpointHeaderOverrideTests.Edit(account, settings =>
        {
            settings["baseUrl"] = address?.GetLeftPart(UriPartial.Authority) ?? "https://upstream.example";
            settings["responsesTransport"] = transport;
            settings["headerOverride"] = profile == "codex" ? new JsonObject { ["Originator"] = "codex_exec" }
                : new JsonObject { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-fixture" };
            settings["headerOverride"]!["X-Request-Ref"] = "{request_id}";
            settings["headerOverride"]!["X-Turn-Ref"] = "{turn_id}";
        });
        return account;
    }

    private static PluginAttemptContext Context(Account account, IPluginHttpClient client, string endpoint,
        bool stream, string credential, params string[] turns)
    {
        var context = ChannelKeysTests.Attempt(account, client);
        var body = ClientSessionResolverTests.Body(endpoint, turns);
        body["stream"] = stream;
        body["prompt_cache_key"] = "keep-client-cache";
        context.Request.Endpoint = endpoint;
        context.Request.Stream = stream;
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(body);
        context.Request.DownstreamRequestHeaders["Authorization"] = ["Bearer " + credential];
        return context;
    }

    private static Mock<IPluginHttpClient> HttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false, HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) => handle(request));
        return client;
    }

    private static HttpResponseMessage Reply(string profile) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(profile == "codex" ? "data: " + Done + "\n\n" : Messages, Encoding.UTF8, "text/event-stream")
    };

    private static async Task<Captured> Capture(HttpRequestMessage request) => new(
        request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase),
        JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject());

    private static async Task Complete(UniversalForwardTerminal terminal, PluginAttemptContext context, bool stream)
    {
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode, result.Response.RawContent is { } bytes ? Encoding.UTF8.GetString(bytes) : "");
        Assert.AreEqual(stream, result.Response.IsStreaming);
        if (result.Response.RawStream is { } output)
            await foreach (var _ in output) { }
        if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
    }

    private static PluginLog[] IdentityLogs(IPluginHost host) => Mock.Get(host.Services.Log).Invocations
        .Select(call => call.Arguments[0]).OfType<PluginLog>().Where(log => log.EventType == "request.identity.resolved").ToArray();

    private sealed record Captured(Dictionary<string, string> Headers, JsonObject Body);
}
