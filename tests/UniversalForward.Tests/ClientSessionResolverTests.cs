using System.Text.Json.Nodes;
using Plugins.UniversalForward;
using Router.Contracts.Domain;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ClientSessionResolverTests
{
    private const string Responses = "/v1/responses";
    private static readonly string[] StableVariables =
        ["session_id", "thread_id", "window_id", "installation_id", "context_window_id", "device_id"];

    [TestMethod]
    [DataRow("/v1/responses")]
    [DataRow("/v1/messages")]
    public void ConversationReusesIdentityAcrossTurnsAndRequestSettings(string endpoint)
    {
        using var resolver = new ClientSessionResolver();
        var firstBody = Body(endpoint, "hello");
        if (endpoint == Responses) firstBody["input"] = "hello";
        firstBody["prompt_cache_key"] = "unchanged-cache";
        var first = resolver.Resolve("channel", "auth", firstBody, endpoint);
        var nextBody = Body(endpoint, "hello", "answer", "next");
        var messages = nextBody[endpoint == Responses ? "input" : "messages"]!.AsArray();
        messages[0]!["content"] = new JsonArray(
            new JsonObject { ["type"] = "input_text", ["text"] = "he" },
            new JsonObject { ["type"] = "text", ["text"] = "llo" });
        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = "Changing request settings" });
        nextBody["model"] = "different-model";
        nextBody["temperature"] = 0.7;
        nextBody["prompt_cache_key"] = "unchanged-cache";
        var next = resolver.Resolve("channel", "auth", nextBody, endpoint);
        Assert.AreEqual("first_turn", first.Reason);
        Assert.AreEqual("history", next.Source);
        Assert.AreEqual(first.Identity, next.Identity);
        Assert.AreEqual(next.Identity, resolver.Resolve("channel", "auth", nextBody, endpoint).Identity);
        Assert.AreEqual(1, resolver.Count);

        var before = ClientProfiles.Variables(firstBody, fallback: first.Identity);
        var after = ClientProfiles.Variables(nextBody, fallback: next.Identity);
        foreach (var name in StableVariables) Assert.AreEqual(before[name], after[name], name);
        Assert.AreNotEqual(before["request_id"], after["request_id"]);
        Assert.AreNotEqual(before["turn_id"], after["turn_id"]);
        var headers = HeaderOverrides.Resolve(new JsonObject { ["Originator"] = "codex_exec", ["X-Request-Ref"] = "{request_id}" },
            new Dictionary<string, string>(), "key", variables: after);
        Assert.AreEqual(after["request_id"], headers["X-Client-Request-Id"]);
        Assert.AreEqual(after["request_id"], headers["X-Request-Ref"]);
        var sent = ClientProfiles.PrepareCodexRequest(nextBody, headers);
        Assert.AreEqual("unchanged-cache", sent["prompt_cache_key"]!.ToString());
        var metadata = JsonNode.Parse(headers["X-Codex-Turn-Metadata"])!;
        Assert.AreEqual(headers["Session-Id"], metadata["session_id"]!.ToString());
        Assert.AreEqual(headers["Thread-Id"], sent["client_metadata"]!["thread_id"]!.ToString());
        Assert.AreEqual(after["turn_id"], sent["client_metadata"]!["turn_id"]!.ToString());
    }

    [TestMethod]
    public void InterleavedChatsAndOldHistoryForksRemainIndependent()
    {
        using var resolver = new ClientSessionResolver();
        var a = Resolve(resolver, "a");
        var b = Resolve(resolver, "b");
        Assert.AreNotEqual(a.Identity, b.Identity);
        Assert.AreEqual(a.Identity, Resolve(resolver, "a", "answer-a", "next-a").Identity);
        Assert.AreEqual(b.Identity, Resolve(resolver, "b", "answer-b", "next-b").Identity);
        var fork = Resolve(resolver, "a", "answer-a", "different-branch");
        Assert.AreNotEqual(a.Identity, fork.Identity);
        Assert.AreEqual("history_not_found", fork.Reason);
        Assert.AreEqual(a.Identity, Resolve(resolver, "a", "answer-a", "next-a", "second-answer", "third").Identity);
        Assert.AreNotEqual(a.Identity, Resolve(resolver, "a").Identity, "An identical opening is still a new chat.");
    }

    [TestMethod]
    public void AmbiguousOpeningsRemainIndependentEvenWithALongerCandidate()
    {
        using var resolver = new ClientSessionResolver();
        var first = Resolve(resolver, "same");
        var other = Resolve(resolver, "same");
        Assert.AreNotEqual(first.Identity, other.Identity);
        var ambiguous = Resolve(resolver, "same", "answer", "next");
        Assert.AreEqual("ambiguous_history", ambiguous.Reason);
        Assert.AreNotEqual(first.Identity, ambiguous.Identity);
        Assert.AreNotEqual(other.Identity, ambiguous.Identity);
        var continued = Resolve(resolver, "same", "answer", "next", "answer-2", "last");
        Assert.AreEqual("ambiguous_history", continued.Reason);
        Assert.AreNotEqual(ambiguous.Identity, continued.Identity);
        Assert.AreEqual(4, resolver.Count);
    }

    [TestMethod]
    public void CandidatesAtDifferentPrefixLengthsAreStillAmbiguous()
    {
        using var resolver = new ClientSessionResolver();
        var longer = Resolve(resolver, "same", "answer", "next");
        var shorter = Resolve(resolver, "same");
        var next = Resolve(resolver, "same", "answer", "next", "answer-2", "last");
        Assert.AreEqual("ambiguous_history", next.Reason);
        Assert.AreNotEqual(shorter.Identity, next.Identity);
        Assert.AreNotEqual(longer.Identity, next.Identity);
    }

    [TestMethod]
    [DataRow("other-channel", "auth", "shared")]
    [DataRow("channel", "other-auth", "shared")]
    [DataRow("channel", "auth", "other-cache")]
    public void ChannelAuthenticationAndCacheHintsPartitionCandidates(string channel, string auth, string cacheKey)
    {
        using var resolver = new ClientSessionResolver();
        var firstBody = Body(Responses, "same");
        firstBody["prompt_cache_key"] = "shared";
        var first = resolver.Resolve("channel", "auth", firstBody, Responses);
        var next = Body(Responses, "same", "answer", "next");
        next["prompt_cache_key"] = cacheKey;
        Assert.AreNotEqual(first.Identity, resolver.Resolve(channel, auth, next, Responses).Identity);
        Assert.AreNotEqual(first.Identity, resolver.Resolve("channel", "auth", firstBody, Responses).Identity,
            "A shared prompt cache key must not turn separate opening requests into one chat.");
    }

    [TestMethod]
    public void LatestUtteranceAndUnmatchedCompactedHistoryStartNewChats()
    {
        using var resolver = new ClientSessionResolver();
        var first = Resolve(resolver, "hello");
        Assert.AreEqual(first.Identity, Resolve(resolver, "hello", "answer", "next").Identity);
        Assert.AreNotEqual(first.Identity, Resolve(resolver, "latest only").Identity);
        var compacted = Resolve(resolver, "summary", "retained answer", "next");
        Assert.AreEqual("history_not_found", compacted.Reason);
        Assert.AreNotEqual(first.Identity, compacted.Identity);
        var empty = resolver.Resolve("channel", "auth", new JsonObject(), Responses);
        Assert.AreEqual("missing_history", empty.Reason);
    }

    [TestMethod]
    public void ToolCallsKeepLinkageAndNormalizeArgumentPropertyOrder()
    {
        using var resolver = new ClientSessionResolver();
        var first = Resolve(resolver, "question");
        var tools = Body(Responses, "question");
        var input = tools["input"]!.AsArray();
        input.Add(JsonNode.Parse("""{"type":"function_call","id":"transport-one","status":"completed","call_id":"call-1","name":"lookup","arguments":"{\"a\":1,\"b\":2}"}"""));
        input.Add(JsonNode.Parse("""{"type":"function_call_output","call_id":"call-1","output":"result"}"""));
        Assert.AreEqual(first.Identity, resolver.Resolve("channel", "auth", tools, Responses).Identity);
        input[1] = JsonNode.Parse("""{"name":"lookup","call_id":"call-1","type":"function_call","arguments":"{ \"b\": 2, \"a\": 1 }"}""");
        input.Add(new JsonObject { ["role"] = "assistant", ["content"] = "done" });
        input.Add(new JsonObject { ["role"] = "user", ["content"] = "next" });
        Assert.AreEqual(first.Identity, resolver.Resolve("channel", "auth", tools, Responses).Identity);
        input[1]!["call_id"] = "different-call";
        Assert.AreNotEqual(first.Identity, resolver.Resolve("channel", "auth", tools, Responses).Identity);
    }

    [TestMethod]
    public void MessagesToolResultsAndImagesParticipateInHistory()
    {
        const string endpoint = "/v1/messages";
        using var resolver = new ClientSessionResolver();
        var body = Body(endpoint, "question");
        var first = resolver.Resolve("channel", "auth", body, endpoint);
        var messages = body["messages"]!.AsArray();
        messages.Add(JsonNode.Parse("""{"role":"assistant","content":[{"type":"tool_use","id":"tool-1","name":"lookup","input":{"b":2,"a":1}}]}"""));
        messages.Add(JsonNode.Parse("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"result"}]}"""));
        Assert.AreEqual(first.Identity, resolver.Resolve("channel", "auth", body, endpoint).Identity);
        messages[1]!["content"]![0]!["input"] = JsonNode.Parse("""{"a":1,"b":2}""");
        messages.Add(JsonNode.Parse("""{"role":"assistant","content":"done"}"""));
        messages.Add(JsonNode.Parse("""{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"original"}}]}"""));
        Assert.AreEqual(first.Identity, resolver.Resolve("channel", "auth", body, endpoint).Identity);
        messages[^1]!["content"]![0]!["source"]!["data"] = "different";
        Assert.AreNotEqual(first.Identity, resolver.Resolve("channel", "auth", body, endpoint).Identity);
    }

    [TestMethod]
    public async Task ConcurrentBranchesAdvanceOnlyOneOriginalHistory()
    {
        using var resolver = new ClientSessionResolver();
        var first = Resolve(resolver, "start");
        var branches = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            Resolve(resolver, "start", "answer", "branch-" + index))));
        Assert.AreEqual(1, branches.Count(result => result.Identity == first.Identity));
        Assert.AreEqual(16, branches.Select(result => result.Identity.SessionId).Distinct().Count());
        for (var i = 0; i < branches.Length; i++)
            Assert.AreEqual(branches[i].Identity, Resolve(resolver, "start", "answer", "branch-" + i).Identity);
    }

    [TestMethod]
    public async Task ConcurrentIdenticalContinuationUsesOneIdentity()
    {
        using var resolver = new ClientSessionResolver();
        var first = Resolve(resolver, "start");
        var continuations = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => Resolve(resolver, "start", "answer", "next"))));
        Assert.IsTrue(continuations.All(result => result.Identity == first.Identity));
        Assert.AreEqual(1, resolver.Count);
    }

    [TestMethod]
    public void IdleExpirySlidesAndCapacityEvictsLeastRecentlyUsed()
    {
        var clock = new ManualTime();
        using var resolver = new ClientSessionResolver(capacity: 2, timeProvider: clock);
        var a = Resolve(resolver, "a");
        var b = Resolve(resolver, "b");
        clock.Advance(TimeSpan.FromHours(23));
        Assert.AreEqual(a.Identity, Resolve(resolver, "a", "answer", "next").Identity);
        Resolve(resolver, "c");
        Assert.AreEqual(2, resolver.Count);
        Assert.AreNotEqual(b.Identity, Resolve(resolver, "b", "answer", "next").Identity);
        // Refresh the other survivor, then verify idle expiry starts from that access.
        var current = Resolve(resolver, "b", "answer", "next");
        clock.Advance(TimeSpan.FromHours(23));
        Assert.AreEqual(current.Identity, Resolve(resolver, "b", "answer", "next").Identity);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.AreNotEqual(current.Identity, Resolve(resolver, "b", "answer", "next").Identity);
        Assert.AreEqual(1, resolver.Count);
    }

    [TestMethod]
    public void AccountRemovalAndDisposalClearInferredSessions()
    {
        using var resolver = new ClientSessionResolver();
        var a = Resolve(resolver, "a");
        var body = Body(Responses, "b");
        var b = resolver.Resolve("other", "auth", body, Responses);
        resolver.RemoveAccount("channel");
        Assert.AreEqual(1, resolver.Count);
        Assert.AreNotEqual(a.Identity, Resolve(resolver, "a", "answer", "next").Identity);
        Assert.AreEqual(b.Identity, resolver.Resolve("other", "auth", Body(Responses, "b", "answer", "next"), Responses).Identity);
        resolver.Dispose();
        Assert.AreEqual(0, resolver.Count);
        Assert.AreEqual("resolver_stopped", Resolve(resolver, "a", "answer", "next").Reason);
        Assert.AreEqual(0, resolver.Count);
        using var restarted = new ClientSessionResolver();
        Assert.AreNotEqual(b.Identity, restarted.Resolve("other", "auth", body, Responses).Identity);
    }

    [TestMethod]
    public void DownstreamAuthenticationIsDigestedBeforeHeadersAreFiltered()
    {
        var request = new AdapterRequest();
        request.RequestHeaders["Authorization"] = "Bearer obsolete-fixture";
        request.DownstreamRequestHeaders["aUtHoRiZaTiOn"] = ["Bearer incoming-fixture"];
        var digest = ForwardHeaders.AuthenticationDigest(request);
        Assert.AreEqual(64, digest.Length);
        request.DownstreamRequestHeaders["aUtHoRiZaTiOn"] = ["bearer incoming-fixture"];
        Assert.AreEqual(digest, ForwardHeaders.AuthenticationDigest(request));
        request.DownstreamRequestHeaders["aUtHoRiZaTiOn"] = ["Bearer different-fixture"];
        Assert.AreNotEqual(digest, ForwardHeaders.AuthenticationDigest(request));
        Assert.IsFalse(ForwardHeaders.ReadClientHeaders(request, new JsonObject()).ContainsKey("Authorization"));
    }

    private static ClientSessionResolution Resolve(ClientSessionResolver resolver, params string[] turns)
        => resolver.Resolve("channel", "auth", Body(Responses, turns), Responses);

    internal static JsonObject Body(string endpoint, params string[] turns) => new()
    {
        ["model"] = "model",
        [endpoint == Responses ? "input" : "messages"] = new JsonArray(turns.Select((text, index) =>
            (JsonNode?)new JsonObject { ["role"] = index % 2 == 0 ? "user" : "assistant", ["content"] = text }).ToArray())
    };

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan interval) => _now += interval;
    }
}
