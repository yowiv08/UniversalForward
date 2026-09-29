using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ClientRequestCompatibilityTests
{
    [TestMethod]
    [DataRow("codex", "/v1/responses")]
    [DataRow("", "/v1/responses")]
    [DataRow("codex", "/v1/chat/completions")]
    [DataRow("claude", "/v1/messages")]
    [DataRow("claude", "/v1/responses")]
    public async Task NormalInvocationCompletesOnlyMatchingProfileAndPinsRetryBody(string profile, string endpoint)
    {
        var original = JsonNode.Parse("""
            {"model":"universalforward/model","stream":false,"input":"Keep my real question.",
             "instructions":"My instructions","include":[],"max_output_tokens":32,
             "messages":[{"role":"user","content":"My message"}],"system":"My system",
             "tools":[{"type":"function","name":"lookup","parameters":{"type":"object"}}]}
            """)!.AsObject();
        var sent = new List<string>();
        var identities = new List<string>();
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) =>
            {
                var json = await request.Content!.ReadAsStringAsync(ct);
                sent.Add(json);
                var body = JsonNode.Parse(json)!;
                Assert.AreEqual("model", body["model"]!.ToString());
                Assert.AreEqual(endpoint == "/v1/messages" ? "channel-key" : "Bearer channel-key",
                    request.Headers.GetValues(endpoint == "/v1/messages" ? "x-api-key" : "Authorization").Single());
                Assert.AreEqual(original["instructions"]!.ToString(), body["instructions"]!.ToString());
                Assert.IsTrue(JsonNode.DeepEquals(original["tools"], body["tools"]));
                Assert.IsFalse(body["stream"]!.GetValue<bool>());
                Assert.AreEqual(32, body["max_output_tokens"]!.GetValue<int>());
                if (profile == "codex")
                {
                    Assert.AreEqual("codex_exec", request.Headers.GetValues("Originator").Single());
                    identities.Add(request.Headers.GetValues("X-Codex-Turn-Metadata").Single());
                }
                if (profile == "claude")
                {
                    Assert.AreEqual("cli", request.Headers.GetValues("x-app").Single());
                    identities.Add(request.Headers.GetValues("x-claude-code-session-id").Single());
                }
                if (profile == "codex" && endpoint == "/v1/responses")
                {
                    Assert.AreEqual("Keep my real question.", body["input"]![0]!["content"]![0]!["text"]!.ToString());
                    Assert.AreEqual("reasoning.encrypted_content", body["include"]![0]!.ToString());
                    Assert.AreEqual(identities[^1], body["client_metadata"]!["x-codex-turn-metadata"]!.ToString());
                }
                else if (profile == "claude" && endpoint == "/v1/messages")
                {
                    Assert.AreEqual("?beta=true", request.RequestUri!.Query);
                    Assert.AreEqual("My message", body["messages"]![0]!["content"]![0]!["text"]!.ToString());
                    Assert.AreEqual("My system", body["system"]![1]!["text"]!.ToString());
                    Assert.AreEqual(identities[^1], JsonNode.Parse(body["metadata"]!["user_id"]!.ToString())!["session_id"]!.ToString());
                }
                else
                {
                    var expected = (JsonObject)original.DeepClone();
                    expected["model"] = "model";
                    Assert.IsTrue(JsonNode.DeepEquals(expected, body));
                }
                return new HttpResponseMessage(sent.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
                    { Content = new StringContent("{}") };
            });
        var account = new Account
        {
            Id = "channel", PluginKey = "universalforward", Platform = "universalforward",
            Credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["settings"] = JsonSerializer.Serialize(new
                {
                    baseUrl = "https://upstream.example", apiKey = "channel-key",
                    headerOverride = profile == "codex" ? new Dictionary<string, string> { ["Originator"] = "codex_exec" }
                        : profile == "claude" ? new Dictionary<string, string> { ["x-app"] = "cli", ["anthropic-beta"] = "claude-code-20250219" } : [],
                    requestPolicy = new ForwardRequestPolicy { MaxRetries = 1 }
                }),
                ["models"] = """["model"]""", ["modelsConfigured"] = "true"
            })
        };
        using var terminal = new UniversalForwardTerminal(PluginTestHost.Create("universalforward"));
        var result = await terminal.InvokeAsync(new PluginAttemptContext
        {
            Account = account, HttpClient = client.Object, PluginKey = "universalforward", PlatformName = "universalforward",
            CancellationToken = CancellationToken.None,
            Request = new AdapterRequest
            {
                Model = "universalforward/model", Endpoint = endpoint, Stream = false,
                OriginalBody = JsonSerializer.SerializeToElement(original)
            }
        });
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual(2, sent.Count);
        Assert.AreEqual(sent[0], sent[1]);
        if (profile.Length != 0) Assert.AreEqual(identities[0], identities[1]);
        Assert.AreEqual("Keep my real question.", original["input"]!.ToString());
    }
}
