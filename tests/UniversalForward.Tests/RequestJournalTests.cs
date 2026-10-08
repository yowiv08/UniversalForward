using System.Globalization;
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
public sealed class RequestJournalTests
{
    [TestMethod]
    public void EndpointsMatchHostLoaderContract()
    {
        foreach (var method in typeof(UniversalForwardTerminal).GetMethods())
        {
            if (!method.IsDefined(typeof(Router.Contracts.Plugins.PluginEndpointAttribute), false)) continue;
            Assert.AreEqual(typeof(Task<PluginResult>), method.ReturnType, method.Name);
            Assert.AreEqual(typeof(PluginHttpContext), method.GetParameters().Single().ParameterType);
        }
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, RequestLogStore.Json);
    private static async Task<string> ReadBody(RequestLogStore store, string id, string part)
    {
        await store.FlushAsync();
        using var body = new MemoryStream();
        var after = -1;
        while (true)
        {
            var result = Json(store.ReadPart(id, part, after));
            foreach (var chunk in result.GetProperty("chunks").EnumerateArray())
                body.Write(Convert.FromBase64String(chunk.GetProperty("base64").GetString()!));
            if (result.GetProperty("done").GetBoolean()) break;
            after = result.GetProperty("next").GetInt32();
        }
        return Encoding.UTF8.GetString(body.ToArray());
    }
    private static RequestLogCapture Begin(RequestLogStore store, string trace = "trace")
        => store.Begin("forward", "channel", "Deleted channel name", "model", "/v1/responses", "direct", trace,
            new { Authorization = "Bearer secret-key" }, """{"reasoning":{"effort":"xhigh"},"input":"private prompt"}""",
            """{"reasoning_effort":"low"}""")!;

    [TestMethod]
    public async Task RuntimeLogsAreRawAndRestartStartsEmpty()
    {
        string id;
        using (var store = new RequestLogStore())
        {
            var capture = Begin(store); id = capture.Id;
            capture.Complete("completed", 200);
            await store.FlushAsync();
            StringAssert.Contains(await ReadBody(store, id, "incoming"), "private prompt");
            StringAssert.Contains(await ReadBody(store, id, "incoming-headers"), "secret-key");
            var rows = Json(store.List(new Dictionary<string, string>())).GetProperty("rows");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.IsFalse(rows.ToString().Contains("secret-key", StringComparison.Ordinal));
            Assert.AreEqual("Deleted channel name", store.Detail(id)!["channelLabel"]!.ToString());
        }
        using var restarted = new RequestLogStore();
        Assert.IsNull(restarted.Detail(id));
        Assert.AreEqual(0L, Json(restarted.Status).GetProperty("bodyBytes").GetInt64());
    }

    [TestMethod]
    public async Task CapturePreservesHeadersPayloadAndUpstreamReasoningAcrossRetries()
    {
        using var store = new RequestLogStore();
        var account = ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "real-api-key" }], 1);
        var settings = ChannelKeysTests.Settings(account);
        settings["headerOverride"] = new JsonObject { ["Authorization"] = "Bearer {api_key}" };
        var fields = new Dictionary<string, string?>(((CustomCredential)account.Credential).Fields)
        { ["settings"] = settings.ToJsonString() };
        account.Credential = new CustomCredential(fields);
        var sent = 0;
        var client = new Mock<IPluginHttpClient>();
        client.Setup(x => x.SendAsync(It.IsAny<HttpRequestMessage>(), false,
            HttpCompletionOption.ResponseHeadersRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(++sent == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent(sent == 1 ? "gateway failed" :
                    """{"reasoning":{"effort":"medium"},"output":[{"type":"message","content":[{"type":"output_text","text":"private answer"}]}]}""",
                    Encoding.UTF8, "application/json")
            });
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(account)) { RequestLogs = store };
        var context = ChannelKeysTests.Attempt(account, client.Object);
        context.Request.OriginalBody = Json(new { model = "model", reasoning = new { effort = "xhigh" }, input = "private prompt" });
        context.Request.Extensions["reasoning_effort"] = "low";
        var result = await terminal.InvokeAsync(context);
        Assert.AreEqual(200, result.Response.StatusCode);
        await store.FlushAsync();
        var row = Json(store.List(new Dictionary<string, string>())).GetProperty("rows")[0];
        var id = row.GetProperty("id").GetString()!;
        Assert.AreEqual("xhigh", row.GetProperty("receivedReasoning").GetProperty("reasoning.effort").GetString());
        Assert.AreEqual("low", row.GetProperty("extensionReasoning").GetProperty("reasoning_effort").GetString());
        Assert.AreEqual("xhigh", row.GetProperty("sentReasoning").GetProperty("reasoning.effort").GetString());
        Assert.AreEqual("medium", row.GetProperty("reportedReasoning").GetProperty("reasoning.effort").GetString());
        Assert.AreEqual(1, row.GetProperty("retries").GetInt32());
        StringAssert.Contains(await ReadBody(store, id, "attempt-2-request-headers"), "real-api-key");
        StringAssert.Contains(await ReadBody(store, id, "attempt-2-request"), "private prompt");
        StringAssert.Contains(await ReadBody(store, id, "attempt-2-response"), "private answer");
        StringAssert.Contains(store.Detail(id)!["attempts"]![0]!["retryReason"]!.ToString(), "502");
    }

    [TestMethod]
    public async Task StreamCaptureIsIncrementalAndDoesNotChangeResponseBytes()
    {
        using var store = new RequestLogStore();
        var capture = Begin(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.example/v1/responses");
        var attempt = capture.Sending(request, Encoding.UTF8.GetBytes("""{"reasoning":{"effort":"xhigh"}}"""));
        const string body = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"你好\"}\r\n\r\ndata: {\"type\":\"response.completed\",\"response\":{\"reasoning\":{\"effort\":\"low\"}}}\r\n\r\n";
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        capture.Received(attempt, response);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var bytes = new byte[7];
        int count;
        while ((count = await stream.ReadAsync(bytes)) > 0) output.Write(bytes, 0, count);
        capture.Complete("completed", 200);
        Assert.AreEqual(body, Encoding.UTF8.GetString(output.ToArray()));
        Assert.AreEqual(body, await ReadBody(store, capture.Id, "attempt-1-response"));
        Assert.AreEqual("low", store.Detail(capture.Id)!["reportedReasoning"]!["reasoning.effort"]!.ToString());
    }

    [TestMethod]
    public async Task LimitsRetentionAndDeletionKeepCorrectAccounting()
    {
        using var store = new RequestLogStore();
        await store.ConfigureAsync(new RequestLogSettings(BodyLimitBytes: 1024), CancellationToken.None);
        var capture = Begin(store);
        capture.AddText("large", new string('x', 4000));
        capture.Complete("completed", 200); await store.FlushAsync();
        Assert.AreEqual(1024, (await ReadBody(store, capture.Id, "large")).Length);
        var part = store.Detail(capture.Id)!["parts"]!.AsArray().Single(p => p!["name"]!.ToString() == "large")!;
        Assert.AreEqual(4000L, part["observedBytes"]!.GetValue<long>());
        Assert.IsTrue(part["truncated"]!.GetValue<bool>());
        Assert.IsTrue(store.Detail(capture.Id)!["incomplete"]!.GetValue<bool>());
        await store.DeleteAsync(capture.Id, CancellationToken.None);
        Assert.IsNull(store.Detail(capture.Id));
        Assert.AreEqual("", await ReadBody(store, capture.Id, "large"));
        Assert.AreEqual(0L, Json(store.Status).GetProperty("bodyBytes").GetInt64());
    }

    [TestMethod]
    public async Task QueueOverflowIsVisibleAndDoesNotWaitForWriter()
    {
        using var store = new RequestLogStore(4);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.Enqueue("", _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); });
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var capture = Begin(store);
            capture.AddText("large", new string('x', 512 * 1024));
            capture.Complete("completed", 200);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.IsTrue(Json(store.Status).GetProperty("droppedWrites").GetInt64() > 0);
        }
        finally { release.Set(); }
        await store.FlushAsync();
        var row = Json(store.List(new Dictionary<string, string>())).GetProperty("rows")[0];
        Assert.IsTrue(row.GetProperty("incomplete").GetBoolean());
    }

    [TestMethod]
    public async Task DisabledAndDisposedStoresDoNotCapture()
    {
        var store = new RequestLogStore();
        await store.ConfigureAsync(new RequestLogSettings(Enabled: false), CancellationToken.None);
        Assert.IsNull(store.Begin("forward", null, null, null, null, null, null, null, null, null));
        store.Dispose();
        Assert.IsFalse(store.Available);
        Assert.IsNull(store.Begin("forward", null, null, null, null, null, null, null, null, null));
    }

    [TestMethod]
    public async Task CapacityEvictsFinishedRecordsButPreservesActiveRecords()
    {
        using var store = new RequestLogStore(4096);
        await store.ConfigureAsync(new RequestLogSettings(CapacityBytes: 1024 * 1024), CancellationToken.None);
        var first = Begin(store);
        first.AddText("large", new string('a', 700 * 1024));
        first.Complete("completed", 200);
        await store.FlushAsync();
        var active = Begin(store);
        active.AddText("large", new string('b', 700 * 1024));
        await store.FlushAsync();
        Assert.IsNull(store.Detail(first.Id));
        Assert.IsNotNull(store.Detail(active.Id));
        var overflow = Begin(store);
        overflow.AddText("large", new string('c', 700 * 1024));
        await store.FlushAsync();
        Assert.IsNotNull(store.Detail(active.Id));
        Assert.IsTrue(store.Detail(overflow.Id)!["incomplete"]!.GetValue<bool>());
        Assert.IsTrue(Json(store.Status).GetProperty("bodyBytes").GetInt64() <= 1024 * 1024);
    }

    [TestMethod]
    public async Task InstancesAndSettingsAreIndependent()
    {
        using var first = new RequestLogStore();
        var capture = Begin(first);
        await first.ConfigureAsync(new RequestLogSettings(MaxRecords: 3), CancellationToken.None);
        using var second = new RequestLogStore();
        Assert.IsTrue(second.Available);
        Assert.IsNull(second.Detail(capture.Id));
        Assert.AreEqual(1000, second.Settings.MaxRecords);
        Assert.AreEqual("running", first.Detail(capture.Id)!["state"]!.ToString());
    }

    [TestMethod]
    public async Task RecordLimitEvictsFinishedAndRejectsOverflowWithoutBlocking()
    {
        using var store = new RequestLogStore();
        await store.ConfigureAsync(new RequestLogSettings(MaxRecords: 1), CancellationToken.None);
        var first = Begin(store); first.Complete("completed", 200); await store.FlushAsync();
        var active = Begin(store); await store.FlushAsync();
        Assert.IsNull(store.Detail(first.Id));
        var overflow = Begin(store); await store.FlushAsync();
        Assert.IsNotNull(store.Detail(active.Id));
        Assert.IsNull(store.Detail(overflow.Id));
        Assert.AreEqual(1, Json(store.Status).GetProperty("records").GetInt32());
        Assert.IsTrue(Json(store.Status).GetProperty("droppedWrites").GetInt64() > 0);
    }

    [TestMethod]
    public async Task ConcurrentRequestsAreIsolatedAndFiltersDoNotLoadBodies()
    {
        using var store = new RequestLogStore(4096);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            var capture = Begin(store, "trace-" + i);
            capture.AddText("private", "private-" + i); capture.Complete("completed", 200);
        })));
        await store.FlushAsync();
        var filtered = Json(store.List(new Dictionary<string, string> { ["trace"] = "trace-7" }));
        Assert.AreEqual(1L, filtered.GetProperty("total").GetInt64());
        var id = filtered.GetProperty("rows")[0].GetProperty("id").GetString()!;
        Assert.AreEqual("private-7", await ReadBody(store, id, "private"));
        Assert.IsFalse(filtered.ToString().Contains("private-7", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PagesIncludeEveryMatchingRecordExactlyOnce()
    {
        using var store = new RequestLogStore(4096);
        for (var i = 0; i < 51; i++) Begin(store, "trace-" + i).Complete("completed", 200);
        await store.FlushAsync();

        foreach (var size in new[] { 10, 25, 50, 100 })
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= (51 + size - 1) / size; page++)
            {
                var result = Json(store.List(new Dictionary<string, string>
                { ["page"] = page.ToString(CultureInfo.InvariantCulture), ["pageSize"] = size.ToString(CultureInfo.InvariantCulture) }));
                Assert.AreEqual(51L, result.GetProperty("total").GetInt64());
                Assert.AreEqual(page, result.GetProperty("page").GetInt32());
                Assert.AreEqual(size, result.GetProperty("pageSize").GetInt32());
                var rows = result.GetProperty("rows");
                Assert.AreEqual(Math.Min(size, 51 - (page - 1) * size), rows.GetArrayLength());
                foreach (var row in rows.EnumerateArray())
                    Assert.IsTrue(ids.Add(row.GetProperty("id").GetString()!), "A record appeared on more than one page.");
            }
            Assert.AreEqual(51, ids.Count);
        }
    }

    [TestMethod]
    public async Task PagesRemainValidAfterDeletionFilteringAndRetention()
    {
        using var store = new RequestLogStore();
        for (var i = 0; i < 11; i++) Begin(store, "trace-" + i).Complete("completed", 200);
        await store.FlushAsync();
        var query = new Dictionary<string, string> { ["page"] = "2", ["pageSize"] = "10" };
        var lastPage = Json(store.List(query));
        Assert.AreEqual(1, lastPage.GetProperty("rows").GetArrayLength());
        await store.DeleteAsync(lastPage.GetProperty("rows")[0].GetProperty("id").GetString(), CancellationToken.None);

        var afterDelete = Json(store.List(query));
        Assert.AreEqual(1, afterDelete.GetProperty("page").GetInt32());
        Assert.AreEqual(10L, afterDelete.GetProperty("total").GetInt64());
        Assert.AreEqual(10, afterDelete.GetProperty("rows").GetArrayLength());

        query["trace"] = afterDelete.GetProperty("rows")[0].GetProperty("trace").GetString()!;
        var filtered = Json(store.List(query));
        Assert.AreEqual(1, filtered.GetProperty("page").GetInt32());
        Assert.AreEqual(1L, filtered.GetProperty("total").GetInt64());
        Assert.AreEqual(1, filtered.GetProperty("rows").GetArrayLength());

        query["trace"] = "no-matching-trace";
        var empty = Json(store.List(query));
        Assert.AreEqual(1, empty.GetProperty("page").GetInt32());
        Assert.AreEqual(0L, empty.GetProperty("total").GetInt64());
        Assert.AreEqual(0, empty.GetProperty("rows").GetArrayLength());

        query.Remove("trace");
        await store.ConfigureAsync(new RequestLogSettings(MaxRecords: 3), CancellationToken.None);
        var afterRetention = Json(store.List(query));
        Assert.AreEqual(1, afterRetention.GetProperty("page").GetInt32());
        Assert.AreEqual(3L, afterRetention.GetProperty("total").GetInt64());
        Assert.AreEqual(3, afterRetention.GetProperty("rows").GetArrayLength());
    }
}
