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
public sealed class RequestPreparationTests
{
    private const string Completed = """{"type":"response.completed","response":{"id":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":2,"output_tokens":1}}}""";

    [TestMethod]
    public async Task CombinedPreparationAppliesEachRuleOnceAndReusesFinalPayloadForRetries()
    {
        var account = Account(retries: 1);
        var original = Object("""
            {"model":"universalforward/model","stream":false,"instructions":"old instructions",
             "previous_response_id":"previous","reasoning":{"effort":"low","summary":"auto"},
             "input":[{"role":"developer","content":"old developer"},
                      {"id":"user-id","role":"user","content":"question"},
                      {"id":"reasoning-id","type":"reasoning","encrypted_content":"private reasoning","summary":[]},
                      {"id":"assistant-id","role":"assistant","content":"answer"},
                      {"id":"call-id","type":"function_call","call_id":"call","name":"lookup","arguments":"{\"id\":\"keep\"}"},
                      {"id":"result-id","type":"function_call_output","call_id":"call","output":"result"}],
             "tools":[{"type":"function","name":"lookup","parameters":{"properties":{"id":{"type":"string"}}}}]}
            """);
        var bodies = new List<string>();
        var client = Client(async (request, ct) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("retry") }
                : Reply();
        });
        using var store = new RequestLogStore();
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account)) { RequestLogs = store };
        var context = Context(account, client.Object, original);
        var originalText = context.Request.OriginalBody!.Value.GetRawText();

        var result = await terminal.InvokeAsync(context);

        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.IsFalse(result.Response.IsStreaming);
        Assert.AreEqual(2, bodies.Count);
        Assert.AreEqual(bodies[0], bodies[1]);
        var sent = Object(bodies[0]);
        Assert.AreEqual("model", sent["model"]!.GetValue<string>());
        Assert.IsTrue(sent["stream"]!.GetValue<bool>());
        Assert.AreEqual("max", sent["reasoning"]!["effort"]!.GetValue<string>());
        Assert.AreEqual("auto", sent["reasoning"]!["summary"]!.GetValue<string>());
        Assert.AreEqual("channel instructions", sent["instructions"]!.GetValue<string>());
        Assert.IsFalse(sent.ContainsKey("previous_response_id"));
        Assert.AreEqual(4, sent["input"]!.AsArray().Count);
        Assert.IsTrue(sent["input"]!.AsArray().OfType<JsonObject>().All(item => !item.ContainsKey("id")));
        Assert.AreEqual("question", sent["input"]![0]!["content"]!.GetValue<string>());
        Assert.AreEqual("call", sent["input"]![2]!["call_id"]!.GetValue<string>());
        Assert.AreEqual("{\"id\":\"keep\"}", sent["input"]![2]!["arguments"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(original["tools"], sent["tools"]));
        Assert.IsTrue(sent["include"]!.AsArray().Any(value => value!.ToString() == "reasoning.encrypted_content"));
        Assert.IsNotNull(sent["client_metadata"]!["session_id"]);
        Assert.AreEqual(originalText, context.Request.OriginalBody!.Value.GetRawText());
        await store.FlushAsync();
        var row = JsonSerializer.SerializeToNode(store.List(new Dictionary<string, string>()), RequestLogStore.Json)!["rows"]![0]!;
        Assert.AreEqual("low", row["receivedReasoning"]!["reasoning.effort"]!.ToString());
        Assert.AreEqual("max", row["sentReasoning"]!["reasoning.effort"]!.ToString());
    }

    [TestMethod]
    public async Task IdentityMatchesOriginalHistoryEvenWhenStrippingMakesDifferentHistoriesEqual()
    {
        var account = Account();
        var captured = new List<(string Session, JsonObject Body)>();
        var client = Client(async (request, ct) =>
        {
            captured.Add((request.Headers.GetValues("Session-Id").Single(),
                Object(await request.Content!.ReadAsStringAsync(ct))));
            return Reply();
        });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account));
        var first = Object("""
            {"model":"universalforward/model","prompt_cache_key":"client-cache","input":[
              {"role":"user","content":"question"},
              {"id":"reasoning-one","type":"reasoning","encrypted_content":"first-history","summary":[]}]}
            """);
        var other = (JsonObject)first.DeepClone();
        other["input"]![1]!["id"] = "reasoning-two";
        other["input"]![1]!["encrypted_content"] = "other-history";
        var followup = (JsonObject)first.DeepClone();
        followup["input"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = "answer" });
        followup["input"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["content"] = "next question" });
        foreach (var body in new[] { first, other, followup })
        {
            var context = Context(account, client.Object, body);
            context.Request.DownstreamRequestHeaders["Authorization"] = ["Bearer client-key"];
            var originalText = context.Request.OriginalBody!.Value.GetRawText();
            Assert.AreEqual(200, (await terminal.InvokeAsync(context)).Response.StatusCode);
            Assert.AreEqual(originalText, context.Request.OriginalBody!.Value.GetRawText());
        }

        Assert.AreEqual(3, captured.Count);
        Assert.IsTrue(JsonNode.DeepEquals(captured[0].Body["input"], captured[1].Body["input"]));
        Assert.AreNotEqual(captured[0].Session, captured[1].Session);
        Assert.AreEqual(captured[0].Session, captured[2].Session);
        foreach (var (session, body) in captured)
        {
            Assert.AreEqual(session, body["client_metadata"]!["session_id"]!.GetValue<string>());
            Assert.IsFalse(body["input"]!.AsArray().Any(item => item?["type"]?.ToString() == "reasoning"));
            Assert.AreEqual("client-cache", body["prompt_cache_key"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task FailedResponseKeepsDownstreamBodyWithoutWritingItToSystemLog()
    {
        const string rawBody = "private upstream response body that must not enter logs";
        var account = Account();
        var host = ChannelKeysTests.Host(account);
        var client = Client((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(rawBody, Encoding.UTF8, "text/plain")
            };
            response.Headers.Add("x-request-id", "upstream-request");
            return Task.FromResult(response);
        });
        using var terminal = new UniversalForwardTerminal(host);

        var result = await terminal.InvokeAsync(Context(account, client.Object, Object("""{"input":"question"}""")));

        Assert.AreEqual(502, result.Response.StatusCode);
        Assert.AreEqual(rawBody, Encoding.UTF8.GetString(result.Response.RawContent!));
        var logs = Mock.Get(host.Services.Log).Invocations.Select(call => call.Arguments[0]).OfType<PluginLog>().ToArray();
        Assert.IsFalse(JsonSerializer.Serialize(logs).Contains(rawBody, StringComparison.Ordinal));
        var failure = logs.Single(log => log.EventType == "request.upstream.failed");
        Assert.AreEqual(502, failure.StatusCode);
        Assert.AreEqual("upstream-request", Object(failure.DetailsJson!)["upstreamRequestId"]!.GetValue<string>());
    }

    private static Account Account(int retries = 0)
    {
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "test-key" }], retries);
        ReasoningPolicyTests.Edit(account, settings =>
        {
            settings["headerOverride"] = new JsonObject { ["Originator"] = "codex_exec" };
            settings["stripResponsesResourceState"] = true;
            settings["systemPromptPolicy"] = new JsonObject { ["mode"] = "replace", ["text"] = "channel instructions" };
            settings["reasoningPolicy"] = JsonSerializer.SerializeToNode(new ReasoningPolicy
            {
                Defaults = new() { ["responses"] = new ReasoningRule
                {
                    Mode = "map", DefaultEffort = "high",
                    Mappings = [new() { From = "low", To = "max" }, new() { From = "max", To = "high" }]
                } }
            }, RequestLogStore.Json);
        });
        return account;
    }

    private static PluginAttemptContext Context(Account account, IPluginHttpClient client, JsonObject body)
    {
        var context = ChannelKeysTests.Attempt(account, client);
        context.Request.Model = "universalforward/model";
        context.Request.OriginalBody = JsonSerializer.SerializeToElement(body);
        return context;
    }

    private static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();

    private static Mock<IPluginHttpClient> Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var client = new Mock<IPluginHttpClient>(MockBehavior.Strict);
        client.Setup(value => value.SendAsync(It.IsAny<HttpRequestMessage>(), false,
                HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .Returns((HttpRequestMessage request, bool _, HttpCompletionOption _, CancellationToken ct) => send(request, ct));
        return client;
    }

    private static HttpResponseMessage Reply() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: " + Completed + "\n\n", Encoding.UTF8, "text/event-stream")
    };
}
