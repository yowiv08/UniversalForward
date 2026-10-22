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
public sealed class ResponsesResourceStateForwardingTests
{
    private const string ResponseBody = """ { "id":"resp_upstream", "status":"completed", "encrypted_content":"keep response state", "output":[{"id":"msg_upstream","type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}] } """;
    private const string Completed = """{"type":"response.completed","response":{"id":"resp_upstream","status":"completed","encrypted_content":"keep response state","output":[{"id":"msg_upstream","type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}]}}""";

    [TestMethod]
    [DataRow(false, "/v1/responses", false)]
    [DataRow(true, "/v1/responses", false)]
    [DataRow(false, "/v1/responses", true)]
    [DataRow(true, "/v1/responses", true)]
    [DataRow(true, "/v1/messages", false)]
    [DataRow(true, "/v1/messages", true)]
    public async Task HttpStrippingUsesChannelSettingAndPinsRetriesWithoutChangingResponse(bool enabled, string endpoint, bool codex)
    {
        var original = History();
        var originalJson = original.ToJsonString();
        var payloads = new List<string>();
        var client = Client(async (request, ct) =>
        {
            Assert.AreEqual(endpoint, request.RequestUri!.AbsolutePath);
            payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(payloads.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created)
            {
                Content = new StringContent(payloads.Count == 1 ? "retry" : ResponseBody, Encoding.UTF8, "application/json")
            };
        });
        var account = Account(new Uri("https://upstream.example"), enabled, codex, retries: 1);
        var context = Context(account, client.Object, original, endpoint);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(context);

        Assert.AreEqual(201, result.Response.StatusCode);
        Assert.AreEqual("application/json; charset=utf-8", result.Response.ContentType);
        Assert.AreEqual(ResponseBody, Encoding.UTF8.GetString(result.Response.RawContent!));
        Assert.AreEqual(2, payloads.Count);
        Assert.AreEqual(payloads[0], payloads[1]);
        var sent = JsonNode.Parse(payloads[0])!.AsObject();
        AssertHistory(sent, enabled && endpoint == "/v1/responses");
        Assert.AreEqual("model", sent["model"]!.ToString());
        Assert.IsTrue(JsonNode.DeepEquals(original["tools"], sent["tools"]));
        Assert.AreEqual("Keep my instructions", sent["instructions"]!.ToString());
        Assert.AreEqual("message.output_text.logprobs", sent["include"]![0]!.ToString());
        if (codex && endpoint == "/v1/responses")
        {
            Assert.IsTrue(sent["include"]!.AsArray().Any(value => value!.ToString() == "reasoning.encrypted_content"));
            Assert.IsTrue(sent["stream"]!.GetValue<bool>());
            Assert.IsNotNull(sent["client_metadata"]!["session_id"]);
            Assert.IsNotNull(sent["prompt_cache_key"]);
        }
        Assert.AreEqual(originalJson, original.ToJsonString());
        Assert.AreEqual(originalJson, context.Request.OriginalBody!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("/v1/messages", "responses", "/v1/responses", true)]
    [DataRow("/v1/responses", "messages", "/v1/messages", false)]
    public async Task StrippingFollowsSelectedUpstreamProtocol(string incoming, string protocol, string target, bool stripped)
    {
        JsonObject? sent = null;
        var client = Client(async (request, ct) =>
        {
            Assert.AreEqual(target, request.RequestUri!.AbsolutePath);
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true);
        EndpointHeaderOverrideTests.Edit(account, settings => settings["modelProtocols"] = JsonSerializer.SerializeToNode(
            new Dictionary<string, ModelProtocolOptions>
            {
                ["model"] = new() { Protocols = [protocol], PreferredProtocol = protocol }
            }));
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, History(), incoming));

        Assert.AreEqual(200, result.Response.StatusCode);
        AssertHistory(sent!, stripped);
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    public async Task StreamingResponseKeepsResourceFieldsAndExactSseBytes()
    {
        var frame = Frame(Completed);
        var client = Client(async (request, ct) =>
        {
            AssertHistory(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(), stripped: true);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(frame, Encoding.UTF8, "text/event-stream")
            };
        });
        var account = Account(new Uri("https://upstream.example"), enabled: true);
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));

        var result = await terminal.InvokeAsync(Context(account, client.Object, History(stream: true), stream: true));

        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.IsTrue(result.Response.IsStreaming);
        Assert.AreEqual(frame, await ReadAll(result.Response.RawStream!));
        Assert.AreEqual(1, client.Invocations.Count);
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task WebSocketPayloadUsesSameStrippingAfterCodexPreparation(bool enabled, bool codex, bool stream)
    {
        JsonObject? sent = null;
        await using var server = new WebSocketLoopback(async (peer, _, ct) =>
        {
            using var socket = await peer.AcceptAsync(ct);
            sent = JsonNode.Parse(await WebSocketLoopback.ReceiveAsync(socket, ct))!.AsObject();
            await WebSocketLoopback.SendAsync(socket, Completed, ct);
        });
        var account = Account(server.Address, enabled, codex, websocket: true);
        var host = ChannelKeysTests.Host(account);
        Mock.Get(host.Services.Http).Setup(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => server.CreateClient());
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        var original = History(stream);
        var originalJson = original.ToJsonString();
        var context = Context(account, client.Object, original, stream: stream);
        using var terminal = new UniversalForwardTerminal(host);

        var result = await terminal.InvokeAsync(context);

        Assert.AreEqual(200, result.Response.StatusCode);
        if (stream) Assert.AreEqual(Frame(Completed), await ReadAll(result.Response.RawStream!));
        else
        {
            var response = JsonNode.Parse(result.Response.RawContent!)!;
            Assert.AreEqual("resp_upstream", response["id"]!.ToString());
            Assert.AreEqual("keep response state", response["encrypted_content"]!.ToString());
            Assert.AreEqual("msg_upstream", response["output"]![0]!["id"]!.ToString());
        }
        await server.Completion;
        Assert.AreEqual("response.create", sent!["type"]!.ToString());
        Assert.IsNull(sent["stream"]);
        AssertHistory(sent, enabled);
        Assert.AreEqual("message.output_text.logprobs", sent["include"]![0]!.ToString());
        if (codex)
        {
            Assert.IsTrue(sent["include"]!.AsArray().Any(value => value!.ToString() == "reasoning.encrypted_content"));
            Assert.IsNotNull(sent["client_metadata"]!["session_id"]);
        }
        Assert.AreEqual(originalJson, original.ToJsonString());
        Assert.AreEqual(originalJson, context.Request.OriginalBody!.Value.GetRawText());
        Assert.AreEqual(0, client.Invocations.Count);
    }

    private static JsonObject History(bool stream = false)
    {
        var body = JsonNode.Parse("""
        {"model":"universalforward/model","stream":false,"id":"keep top id","previous_response_id":"resp_old",
         "instructions":"Keep my instructions","include":["message.output_text.logprobs"],
         "input":[
           {"role":"user","content":[{"type":"input_text","text":"question","id":"keep nested id","encrypted_content":"keep nested encrypted"}]},
           {"type":"reasoning","id":"rs_old","encrypted_content":"old ciphertext","encrypted_extra":"other ciphertext","summary":[{"type":"summary_text","text":"Keep summary"}],"Encrypted_content":"keep case variant"},
           {"type":"function_call","id":"fc_old","call_id":"call_1","name":"lookup","arguments":"{\"id\":\"keep argument id\",\"encrypted_content\":\"keep argument encrypted\"}"},
           {"type":"function_call_output","id":"output_old","call_id":"call_1","output":{"id":"keep output id","encrypted_content":"keep output encrypted"},"previous_response_id":"keep item reference"}],
         "messages":[{"role":"user","content":"question","id":"keep messages id","encrypted_content":"keep messages encrypted"}],
         "tools":[{"type":"function","name":"lookup","parameters":{"type":"object","properties":{"id":{"type":"string"}}}}]}
        """)!.AsObject();
        body["stream"] = stream;
        return body;
    }

    private static void AssertHistory(JsonObject body, bool stripped)
    {
        Assert.AreEqual("keep top id", body["id"]!.ToString());
        var input = body["input"]!.AsArray();
        if (stripped)
        {
            Assert.IsFalse(body.ContainsKey("previous_response_id"));
            Assert.IsFalse(input[1]!.AsObject().ContainsKey("id"));
            Assert.IsFalse(input[1]!.AsObject().ContainsKey("encrypted_content"));
            Assert.IsFalse(input[1]!.AsObject().ContainsKey("encrypted_extra"));
            Assert.IsFalse(input[2]!.AsObject().ContainsKey("id"));
            Assert.IsFalse(input[3]!.AsObject().ContainsKey("id"));
        }
        else
        {
            Assert.AreEqual("resp_old", body["previous_response_id"]!.ToString());
            Assert.AreEqual("rs_old", input[1]!["id"]!.ToString());
            Assert.AreEqual("old ciphertext", input[1]!["encrypted_content"]!.ToString());
            Assert.AreEqual("other ciphertext", input[1]!["encrypted_extra"]!.ToString());
            Assert.AreEqual("fc_old", input[2]!["id"]!.ToString());
            Assert.AreEqual("output_old", input[3]!["id"]!.ToString());
        }
        Assert.AreEqual("keep nested id", input[0]!["content"]![0]!["id"]!.ToString());
        Assert.AreEqual("keep nested encrypted", input[0]!["content"]![0]!["encrypted_content"]!.ToString());
        Assert.AreEqual("keep case variant", input[1]!["Encrypted_content"]!.ToString());
        Assert.AreEqual("Keep summary", input[1]!["summary"]![0]!["text"]!.ToString());
        Assert.AreEqual("call_1", input[2]!["call_id"]!.ToString());
        Assert.AreEqual("lookup", input[2]!["name"]!.ToString());
        Assert.AreEqual("{\"id\":\"keep argument id\",\"encrypted_content\":\"keep argument encrypted\"}", input[2]!["arguments"]!.ToString());
        Assert.AreEqual("call_1", input[3]!["call_id"]!.ToString());
        Assert.AreEqual("keep output id", input[3]!["output"]!["id"]!.ToString());
        Assert.AreEqual("keep output encrypted", input[3]!["output"]!["encrypted_content"]!.ToString());
        Assert.AreEqual("keep item reference", input[3]!["previous_response_id"]!.ToString());
        Assert.AreEqual("keep messages id", body["messages"]![0]!["id"]!.ToString());
        Assert.AreEqual("keep messages encrypted", body["messages"]![0]!["encrypted_content"]!.ToString());
    }

    private static Account Account(Uri address, bool enabled, bool codex = false, int retries = 0, bool websocket = false) => new()
    {
        Id = "channel", PluginKey = "universalforward", Platform = "universalforward",
        Credential = new CustomCredential(new Dictionary<string, string?>
        {
            ["settings"] = JsonSerializer.Serialize(new
            {
                baseUrl = address.GetLeftPart(UriPartial.Authority), apiKey = "channel-secret",
                stripResponsesResourceState = enabled, responsesTransport = websocket ? "websocket" : "http",
                headerOverride = codex ? new Dictionary<string, string> { ["Originator"] = "codex_exec" } : [],
                requestPolicy = new ForwardRequestPolicy
                {
                    MaxRetries = retries, HeaderTimeoutSeconds = 5, TotalTimeoutSeconds = 12, StreamIdleTimeoutSeconds = 5
                }
            }),
            ["models"] = """["model"]""", ["modelsConfigured"] = "true"
        })
    };

    private static PluginAttemptContext Context(Account account, IPluginHttpClient client, JsonObject body,
        string endpoint = "/v1/responses", bool stream = false)
    {
        return new PluginAttemptContext
        {
            Account = account, HttpClient = client, PluginKey = "universalforward", PlatformName = "universalforward",
            CancellationToken = CancellationToken.None,
            Request = new AdapterRequest
            {
                Model = "universalforward/model", Endpoint = endpoint, Stream = stream,
                OriginalBody = JsonSerializer.SerializeToElement(body)
            }
        };
    }

    private static Mock<IPluginHttpClient> Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) => reply(request, ct));
        return client;
    }

    private static string Frame(string json) => $"event: response.completed\ndata: {json}\n\n";

    private static async Task<string> ReadAll(IAsyncEnumerable<ReadOnlyMemory<byte>> stream)
    {
        using var output = new MemoryStream();
        await foreach (var bytes in stream) output.Write(bytes.Span);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
