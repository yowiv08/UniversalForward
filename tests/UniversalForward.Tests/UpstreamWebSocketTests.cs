using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace UniversalForward.Tests;

[TestClass]
public sealed class UpstreamWebSocketTests
{
    private static readonly string[] Models = ["model"];
    private const string Created = """{"type":"response.created","response":{"id":"pending"}}""";
    private const string Delta = """{"type":"response.output_text.delta","delta":"你好😀"}""";
    private const string Done = """{"type":"response.completed","response":{"id":"resp_ws","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"你好😀"}]}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}""";
    private const string Limited = """{"type":"error","status":429,"error":{"type":"rate_limit_error","code":"rate_limit_exceeded","message":"busy"}}""";

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RealHandshakeTranslatesFragmentedEventsAndKeepsRequest(bool stream, bool tls)
    {
        string? payload = null;
        WebSocketLoopback.Peer? received = null;
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            received = peer;
            using var socket = await peer.AcceptAsync(ct);
            payload = await WebSocketLoopback.ReceiveAsync(socket, ct);
            await WebSocketLoopback.SendAsync(socket, Created, ct);
            await WebSocketLoopback.SendAsync(socket, Delta, ct, fragmentSize: 1);
            await WebSocketLoopback.SendAsync(socket, Done, ct);
        }, tls: tls);
        var account = Account(server.Address);
        var handler = new TrackingHandler();
        var host = Host(account, () => server.CreateClient(handler));
        using var store = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(host) { RequestLogs = store };
        var context = Context(account, stream);
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        if (stream)
        {
            Assert.IsTrue(result.Response.IsStreaming);
            Assert.AreEqual(Frame(Created) + Frame(Delta) + Frame(Done), await ReadAll(result.Response.RawStream!));
        }
        else
        {
            Assert.AreEqual("application/json", result.Response.ContentType);
            var body = JsonNode.Parse(result.Response.RawContent!)!;
            Assert.AreEqual("resp_ws", body["id"]!.ToString());
            Assert.AreEqual("你好😀", body["output"]![0]!["content"]![0]!["text"]!.ToString());
            Assert.AreEqual(5, body["usage"]!["total_tokens"]!.GetValue<int>());
        }
        await server.Completion;
        Assert.IsTrue(handler.Disposed);
        Assert.AreEqual("GET /v1/responses HTTP/1.1", received!.RequestLine);
        Assert.AreEqual("Bearer ws-secret", received.Headers["Authorization"]);
        Assert.IsFalse(received.Headers.ContainsKey("Content-Length"));
        var sent = JsonNode.Parse(payload!)!;
        Assert.AreEqual("response.create", sent["type"]!.ToString());
        Assert.AreEqual("model", sent["model"]!.ToString());
        Assert.AreEqual("question", sent["input"]!.ToString());
        Assert.AreEqual("high", sent["reasoning"]!["effort"]!.ToString());
        Assert.IsNull(sent["stream"]);
        Assert.IsNull(sent["background"]);
        Assert.AreEqual(0, Mock.Get(context.HttpClient).Invocations.Count);

        await store.FlushAsync();
        var id = FirstLogId(store);
        var detail = store.Detail(id)!;
        var attempt = detail["attempts"]![0]!;
        Assert.AreEqual(101, attempt["status"]!.GetValue<int>());
        Assert.AreEqual("websocket", attempt["transport"]!.ToString());
        StringAssert.StartsWith(attempt["finalUrl"]!.ToString(), tls ? "wss://" : "ws://");
        var headers = JsonNode.Parse(await LogPart(store, id, "attempt-1-response-headers"))!;
        Assert.IsNotNull(headers["Sec-WebSocket-Accept"]);
        Assert.IsNull(headers["Content-Type"]);
        Assert.AreEqual(Created + "\n" + Delta + "\n" + Done + "\n", await LogPart(store, id, "attempt-1-websocket-events"));
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(204)]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(429)]
    [DataRow(502)]
    public async Task RejectedHandshakeNeverFallsBackOrReportsSuccess(int status)
    {
        const string body = """{"error":{"code":"handshake_rejected","message":"upgrade required"}}""";
        await using var server = new WebSocketLoopback((peer, _, ct) => peer.RejectAsync(status, status == 204 ? "" : body, ct));
        var account = Account(server.Address);
        using var store = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient())) { RequestLogs = store };
        var result = await terminal.InvokeAsync(Context(account, false));
        Assert.AreEqual(status < 300 ? 502 : status, result.Response.StatusCode);
        if (status >= 300) Assert.AreEqual(body, Encoding.UTF8.GetString(result.Response.RawContent!));
        else StringAssert.Contains(Encoding.UTF8.GetString(result.Response.RawContent!), "101 Switching Protocols");
        await server.Completion;
        Assert.AreEqual(1, server.Requests);
        await store.FlushAsync();
        var detail = store.Detail(FirstLogId(store))!;
        Assert.AreEqual(status, detail["upstreamStatus"]!.GetValue<int>());
        Assert.AreEqual(status, detail["attempts"]![0]!["status"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task InvalidUpgradeChallengeFails()
    {
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct, invalidAccept: true);
        });
        var account = Account(server.Address);
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient()));
        Assert.AreEqual(502, (await terminal.InvokeAsync(Context(account, false))).Response.StatusCode);
        await server.Completion;
    }

    [TestMethod]
    [DataRow("handshake")]
    [DataRow("rate_limit")]
    [DataRow("close")]
    public async Task FailureBeforeOutputRetriesWithSameKeyHeadersAndPayload(string failure)
    {
        var payloads = new List<string>();
        var headers = new List<Dictionary<string, string>>();
        await using var server = new WebSocketLoopback(async (peer, index, ct) =>
        {
            headers.Add(peer.Headers);
            if (index == 0 && failure == "handshake") { await peer.RejectAsync(502, "bad gateway", ct); return; }
            using var socket = await peer.AcceptAsync(ct);
            payloads.Add(await WebSocketLoopback.ReceiveAsync(socket, ct));
            if (index == 0)
            {
                await WebSocketLoopback.SendAsync(socket, Created, ct);
                if (failure == "rate_limit") await WebSocketLoopback.SendAsync(socket, Limited, ct);
                else await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "interrupted", ct);
                return;
            }
            await WebSocketLoopback.SendAsync(socket, Delta, ct);
            await WebSocketLoopback.SendAsync(socket, Done, ct);
        }, connections: 2);
        var policy = Policy();
        policy.MaxRetries = 1;
        policy.RateLimitRetryEnabled = failure == "rate_limit";
        policy.EmptyResponseRetryEnabled = failure == "close";
        var account = Account(server.Address, policy: policy);
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient()));
        var result = await terminal.InvokeAsync(Context(account, true));
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(Frame(Delta) + Frame(Done), await ReadAll(result.Response.RawStream!));
        await server.Completion;
        Assert.AreEqual(2, server.Requests);
        Assert.AreEqual(1, payloads.Distinct().Count());
        Assert.AreEqual(headers[0]["Authorization"], headers[1]["Authorization"]);
        Assert.AreEqual(headers[0]["X-Session"], headers[1]["X-Session"]);
        Assert.AreNotEqual(headers[0]["Sec-WebSocket-Key"], headers[1]["Sec-WebSocket-Key"]);
    }

    [TestMethod]
    [DataRow("close")]
    [DataRow("rate_limit")]
    [DataRow("invalid_json")]
    public async Task FailureAfterOutputIsNeverReplayed(string failure)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            await WebSocketLoopback.ReceiveAsync(socket, ct);
            await WebSocketLoopback.SendAsync(socket, Delta, ct);
            await release.Task.WaitAsync(ct);
            if (failure == "close") await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "broken", ct);
            else await WebSocketLoopback.SendAsync(socket, failure == "rate_limit" ? Limited : "not JSON", ct);
        });
        var policy = Policy();
        policy.MaxRetries = 3; policy.RetryStatusCodes = "200,429,502";
        policy.EmptyResponseRetryEnabled = true; policy.RateLimitRetryEnabled = true;
        var account = Account(server.Address, policy: policy);
        var handler = new TrackingHandler();
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient(handler)));
        var result = await terminal.InvokeAsync(Context(account, true));
        await using var output = result.Response.RawStream!.GetAsyncEnumerator();
        Assert.IsTrue(await output.MoveNextAsync());
        Assert.AreEqual(Frame(Delta), Encoding.UTF8.GetString(output.Current.Span));
        release.TrySetResult();
        if (failure == "invalid_json")
            await Assert.ThrowsAsync<InvalidDataException>(async () => { while (await output.MoveNextAsync()) { } });
        else
            await Assert.ThrowsAsync<IOException>(async () => { while (await output.MoveNextAsync()) { } });
        await server.Completion;
        Assert.AreEqual(1, server.Requests);
        Assert.IsTrue(handler.Disposed);
    }

    [TestMethod]
    [DataRow("not JSON")]
    [DataRow("{}")]
    [DataRow("{\"type\":42}")]
    [DataRow("{\"type\":\"bad\\nframe\"}")]
    public async Task InvalidEventFailsBeforeOutput(string message)
    {
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            await WebSocketLoopback.ReceiveAsync(socket, ct);
            await WebSocketLoopback.SendAsync(socket, message, ct);
        });
        var account = Account(server.Address);
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient()));
        Assert.AreEqual(502, (await terminal.InvokeAsync(Context(account, true))).Response.StatusCode);
        await server.Completion;
    }

    [TestMethod]
    [DataRow("header", 504)]
    [DataRow("idle", 502)]
    [DataRow("total", 504)]
    public async Task TimeoutsAreBoundedWithoutFallback(string stage, int status)
    {
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = stage == "header" ? null : await peer.AcceptAsync(ct);
            if (socket is not null) await WebSocketLoopback.ReceiveAsync(socket, ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        var policy = Policy();
        if (stage == "header") policy.HeaderTimeoutSeconds = 1;
        if (stage == "idle") policy.StreamIdleTimeoutSeconds = 1;
        if (stage == "total") policy.TotalTimeoutSeconds = 1;
        var account = Account(server.Address, policy: policy);
        var handler = new TrackingHandler();
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient(handler)));
        var result = await terminal.InvokeAsync(Context(account, true)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(status, result.Response.StatusCode);
        Assert.IsTrue(handler.Disposed);
        Assert.AreEqual(1, server.Requests);
    }

    [TestMethod]
    public async Task EarlyDisposalReleasesTheSocket()
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            await WebSocketLoopback.ReceiveAsync(socket, ct);
            await WebSocketLoopback.SendAsync(socket, Delta, ct);
            try { await socket.ReceiveAsync(new byte[16].AsMemory(), ct); }
            catch (WebSocketException) { }
            disconnected.TrySetResult();
        });
        var account = Account(server.Address);
        var handler = new TrackingHandler();
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient(handler)));
        var result = await terminal.InvokeAsync(Context(account, true));
        await using (var output = result.Response.RawStream!.GetAsyncEnumerator())
            Assert.IsTrue(await output.MoveNextAsync());
        await result.Response.Lifetime!.DisposeAsync();
        Assert.IsTrue(handler.Disposed);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.Completion;
    }

    [TestMethod]
    public async Task CallerCancellationBeforeOutputReleasesTheSocket()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            await WebSocketLoopback.ReceiveAsync(socket, ct);
            received.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        var account = Account(server.Address);
        var handler = new TrackingHandler();
        using var cancellation = new CancellationTokenSource();
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient(handler)));
        var request = terminal.InvokeAsync(Context(account, true, cancellation.Token));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await request);
        Assert.IsTrue(handler.Disposed);
        Assert.AreEqual(1, server.Requests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BinaryOrInvalidUtf8CannotSucceed(bool binary)
    {
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            await WebSocketLoopback.ReceiveAsync(socket, ct);
            await socket.SendAsync(new byte[] { 0xff }.AsMemory(), binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct);
        });
        var account = Account(server.Address);
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient()));
        Assert.AreEqual(502, (await terminal.InvokeAsync(Context(account, true))).Response.StatusCode);
        await server.Completion;
    }

    [TestMethod]
    public async Task BackgroundRequestIsRejectedBeforeSending()
    {
        var account = Account(new Uri("https://upstream.invalid/v1/responses"));
        var handler = new NeverSendHandler();
        using var terminal = new UniversalForwardTerminal(Host(account, () => new HttpClient(handler)));
        var context = Context(account, false);
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(new { model = "model", input = "question", background = true });
        Assert.AreEqual(400, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.IsFalse(handler.Sent);
    }

    [TestMethod]
    [DataRow("http", "/v1/responses")]
    [DataRow("websocket", "/v1/messages")]
    public async Task HttpTransportAndMessagesKeepUsingHttp(string transport, string endpoint)
    {
        var account = Account(new Uri("https://upstream.invalid/v1/responses"));
        var settings = ChannelKeysTests.Settings(account);
        settings["responsesTransport"] = transport;
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() });
        var context = Context(account, false);
        context.Request.Endpoint = endpoint;
        var calls = 0;
        Mock.Get(context.HttpClient).Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken _) =>
            {
                calls++;
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual(endpoint, request.RequestUri!.AbsolutePath);
                Assert.IsFalse(request.Headers.Contains("Upgrade"));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    endpoint == "/v1/responses" ? JsonNode.Parse(Done)!["response"]!.ToJsonString()
                    : """{"content":[{"type":"text","text":"OK"}]}""", Encoding.UTF8, "application/json") };
            });
        var host = ChannelKeysTests.Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
        Assert.AreEqual(1, calls);
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    public async Task ModelDiscoveryKeepsUsingHttpWithWebSocketEnabled()
    {
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            Assert.AreEqual("GET /v1/models HTTP/1.1", peer.RequestLine);
            Assert.IsFalse(peer.Headers.ContainsKey("Upgrade"));
            await peer.RejectAsync(200, """{"data":[{"id":"model"}]}""", ct);
        });
        var account = Account(server.Address);
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient()));
        Assert.AreEqual(200, (await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { id = account.Id }))).StatusCode);
        await server.Completion;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WebSocketHandshakeActuallyTraversesPoolProxy(bool tls)
    {
        string? requestLine = null;
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            requestLine = peer.RequestLine;
            using var socket = await peer.AcceptAsync(ct);
            await WebSocketLoopback.ReceiveAsync(socket, ct);
            await WebSocketLoopback.SendAsync(socket, Done, ct);
        }, tls: tls);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var proxyAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var tunnel = ProxyAsync();
        var account = Account(new Uri($"{(tls ? "https" : "http")}://upstream.invalid/v1/responses"), "proxyPool");
        var host = Host(account, () => server.CreateClient(proxy: proxyAddress), "proxyPool");
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(200, (await terminal.InvokeAsync(Context(account, false))).Response.StatusCode);
        await server.Completion;
        var proxyHead = await tunnel;
        StringAssert.StartsWith(proxyHead, $"CONNECT upstream.invalid:{(tls ? 443 : 80)} HTTP/1.1\r\n");
        Assert.AreEqual("GET /v1/responses HTTP/1.1", requestLine);
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        Mock.Get(host.Services.Http.Pool).Verify(x => x.CreateClientAsync(
            It.Is<ProxyPoolHttpClientOptions>(options => !options.AllowDirectFallback && !options.AllowAutoRedirect),
            It.IsAny<CancellationToken>()), Times.Once);

        async Task<string> ProxyAsync()
        {
            using var downstream = await listener.AcceptTcpClientAsync(timeout.Token);
            var input = downstream.GetStream();
            var head = await WebSocketLoopback.ReadHeadersAsync(input, timeout.Token);
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, server.Address.Port, timeout.Token);
            await input.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), timeout.Token);
            var output = upstream.GetStream();
            try { await Task.WhenAll(input.CopyToAsync(output, timeout.Token), output.CopyToAsync(input, timeout.Token)); }
            catch (IOException) { /* A completed response disposes the upgraded connection. */ }
            return head;
        }
    }

    [TestMethod]
    public async Task EmptyPoolDoesNotOpenDirectWebSocket()
    {
        var account = Account(new Uri("https://upstream.invalid/v1/responses"), "proxyPool");
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http.Pool).Setup(x => x.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProxyPoolUnavailableException());
        using var terminal = new UniversalForwardTerminal(host);
        Assert.AreEqual(503, (await terminal.InvokeAsync(Context(account, true))).Response.StatusCode);
        Mock.Get(host.Services.Http).Verify(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ChannelConnectionTestUsesConfiguredWebSocket(bool stream, bool codex)
    {
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            var payload = JsonNode.Parse(await WebSocketLoopback.ReceiveAsync(socket, ct))!;
            Assert.AreEqual("response.create", payload["type"]!.ToString());
            if (codex)
            {
                Assert.AreEqual("codex_exec", peer.Headers["Originator"]);
                Assert.AreEqual(peer.Headers["Session-Id"], payload["client_metadata"]!["session_id"]!.ToString());
                Assert.IsFalse(payload["store"]!.GetValue<bool>());
                Assert.IsNull(payload["stream"]);
            }
            await WebSocketLoopback.SendAsync(socket, Created, ct);
            await WebSocketLoopback.SendAsync(socket, Done, ct);
        });
        var account = Account(server.Address);
        if (codex)
        {
            var settings = ChannelKeysTests.Settings(account);
            settings["headerOverride"] = new JsonObject { ["Originator"] = "codex_exec", ["Authorization"] = "Bearer {api_key}" };
            account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
            { ["settings"] = settings.ToJsonString() });
        }
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient()));
        PluginJobRegistration? registration = null;
        var builder = new Mock<IPluginBuilder>();
        builder.Setup(x => x.Job(It.IsAny<PluginJobRegistration>())).Callback<PluginJobRegistration>(r => registration = r);
        terminal.Configure(builder.Object);
        var result = await registration!.ExecuteAsync(new PluginJobContext("job", "universalforward", "universalforward",
            JsonSerializer.SerializeToElement(new { accountId = account.Id, models = Models, endpoint = "/v1/responses", stream }),
            _ => { }, CancellationToken.None));
        Assert.IsTrue(result!.Value.GetProperty("rows")[0].GetProperty("success").GetBoolean(), result.ToString());
        Assert.AreEqual("websocket", result.Value.GetProperty("rows")[0].GetProperty("transport").GetString());
        StringAssert.Contains(result.Value.GetProperty("rows")[0].GetProperty("response").GetString()!, "response.completed");
        await server.Completion;
    }

    [TestMethod]
    public async Task TransportSettingDefaultsValidatesAndSurvivesUnrelatedSave()
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "secret" }]);
        var host = ChannelKeysTests.Host(account);
        using var terminal = new UniversalForwardTerminal(host);
        var listed = await terminal.ListAccountsAsync(ChannelKeysTests.Context(new { }));
        StringAssert.Contains(JsonSerializer.Serialize(listed.Body), "\"responsesTransport\":\"http\"");
        var invalid = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = "Channel", responsesTransport = "invalid" }));
        Assert.AreEqual(400, invalid.StatusCode);
        var enabled = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = "Channel", responsesTransport = "websocket" }));
        Assert.AreEqual(200, enabled.StatusCode);
        var saved = await terminal.SaveAccountAsync(ChannelKeysTests.Context(new { id = account.Id, label = "Renamed" }));
        Assert.AreEqual(200, saved.StatusCode);
        Assert.AreEqual("websocket", ChannelKeysTests.Settings(account)["responsesTransport"]!.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReasoningMappingReachesRealWebSocketPayload(bool stream)
    {
        JsonObject? sent = null;
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            sent = JsonNode.Parse(await WebSocketLoopback.ReceiveAsync(socket, ct))!.AsObject();
            await WebSocketLoopback.SendAsync(socket, Done, ct);
        });
        var account = Account(server.Address);
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["headerOverride"]!["Originator"] = "codex_exec";
            settings["reasoningPolicy"] = JsonNode.Parse("""{"defaults":{"responses":{"mode":"fixed","fixedEffort":"low"}},"models":{"model":{"responses":{"mode":"map","mappings":[{"from":"high","to":"max"}]}}}}""");
        });
        using var terminal = new UniversalForwardTerminal(Host(account, () => server.CreateClient()));
        var context = Context(account, stream);
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        if (stream) _ = await ReadAll(result.Response.RawStream!);
        await server.Completion;
        Assert.AreEqual("response.create", sent!["type"]!.ToString());
        Assert.AreEqual("max", sent["reasoning"]!["effort"]!.ToString());
        Assert.AreEqual("high", context.Request.OriginalBody!.Value.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    private static ForwardRequestPolicy Policy() => new()
    {
        HeaderTimeoutSeconds = 5, TotalTimeoutSeconds = 12, StreamIdleTimeoutSeconds = 5,
        ResponseMaxRetries = 1, ResponseRetryIntervalSeconds = 1
    };

    private static Account Account(Uri address, string mode = "direct", ForwardRequestPolicy? policy = null)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "ws-secret" }]);
        var settings = ChannelKeysTests.Settings(account);
        settings["baseUrl"] = address.GetLeftPart(UriPartial.Authority);
        settings["responsesTransport"] = "websocket";
        settings["networkMode"] = mode;
        settings["headerOverride"] = new JsonObject { ["Authorization"] = "Bearer {api_key}", ["X-Session"] = "{session_id}" };
        settings["requestPolicy"] = JsonSerializer.SerializeToNode(policy ?? Policy(), RequestLogStore.Json);
        account.Credential = new CustomCredential(new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() });
        return account;
    }

    private static IPluginHost Host(Account account, Func<HttpClient> create, string mode = "direct")
    {
        var host = ChannelKeysTests.Host(account);
        if (mode == "proxyPool")
            Mock.Get(host.Services.Http.Pool).Setup(x => x.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(create);
        else
            Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(create);
        return host;
    }

    private static PluginAttemptContext Context(Account account, bool stream, CancellationToken token = default) => new()
    {
        PluginKey = "universalforward", PlatformName = "universalforward", Account = account,
        HttpClient = Mock.Of<IPluginHttpClient>(MockBehavior.Strict), CancellationToken = token,
        Request = new AdapterRequest
        {
            Model = "model", Endpoint = "/v1/responses", Stream = stream,
            OriginalBody = JsonSerializer.SerializeToElement(new { model = "model", input = "question", stream, background = false, reasoning = new { effort = "high" } })
        }
    };

    private static string Frame(string json)
    {
        using var document = JsonDocument.Parse(json);
        return $"event: {document.RootElement.GetProperty("type").GetString()}\ndata: {json}\n\n";
    }

    private static async Task<string> ReadAll(IAsyncEnumerable<ReadOnlyMemory<byte>> stream)
    {
        using var output = new MemoryStream();
        await foreach (var bytes in stream) output.Write(bytes.Span);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string FirstLogId(RequestLogStore store)
        => JsonSerializer.SerializeToElement(store.List(new Dictionary<string, string>()), RequestLogStore.Json)
            .GetProperty("rows")[0].GetProperty("id").GetString()!;

    private static async Task<string> LogPart(RequestLogStore store, string id, string part)
    {
        await store.FlushAsync();
        var result = JsonSerializer.SerializeToElement(store.ReadPart(id, part, -1), RequestLogStore.Json);
        using var bytes = new MemoryStream();
        foreach (var chunk in result.GetProperty("chunks").EnumerateArray()) bytes.Write(Convert.FromBase64String(chunk.GetProperty("base64").GetString()!));
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed class TrackingHandler : DelegatingHandler
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class NeverSendHandler : HttpMessageHandler
    {
        public bool Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = true;
            throw new InvalidOperationException("Unexpected network send");
        }
    }
}
