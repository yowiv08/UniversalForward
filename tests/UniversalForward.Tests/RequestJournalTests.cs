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
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "universalforward-journal-tests", Guid.NewGuid().ToString("N"));
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
    public async Task RestartRetainsRawBodiesAndMarksOnlyAbandonedRequestsInterrupted()
    {
        var directory = NewDirectory(); string done, abandoned;
        using (var store = new RequestLogStore(directory))
        {
            Assert.IsTrue(store.Available, JsonSerializer.Serialize(store.Status));
            var capture = Begin(store); done = capture.Id;
            capture.Complete("completed", 200);
            abandoned = Begin(store).Id;
            await store.FlushAsync();
        }
        using var restarted = new RequestLogStore(directory);
        Assert.AreEqual("completed", restarted.Detail(done)!["state"]!.ToString());
        Assert.AreEqual("interrupted", restarted.Detail(abandoned)!["state"]!.ToString());
        StringAssert.Contains(await ReadBody(restarted, done, "incoming"), "private prompt");
        StringAssert.Contains(await ReadBody(restarted, done, "incoming-headers"), "secret-key");
        var rows = Json(restarted.List(new Dictionary<string, string>())).GetProperty("rows");
        Assert.AreEqual(2, rows.GetArrayLength());
        Assert.IsFalse(rows.ToString().Contains("secret-key", StringComparison.Ordinal));
        Assert.IsFalse(rows.ToString().Contains("private prompt", StringComparison.Ordinal));
        Assert.AreEqual("Deleted channel name", restarted.Detail(done)!["channelLabel"]!.ToString());
    }

    [TestMethod]
    public async Task CapturePreservesHeadersPayloadAndUpstreamReasoningAcrossRetries()
    {
        using var store = new RequestLogStore(NewDirectory());
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
        using var store = new RequestLogStore(NewDirectory());
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
        var directory = NewDirectory();
        using var store = new RequestLogStore(directory);
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
        using var connection = store.Open();
        using var command = RequestLogStore.Command(connection, "SELECT bytes FROM accounting WHERE id=1");
        Assert.AreEqual(0L, (long)command.ExecuteScalar()!);
    }

    [TestMethod]
    public async Task QueueOverflowIsVisibleAndDoesNotWaitForWriter()
    {
        using var store = new RequestLogStore(NewDirectory(), 4);
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
    public async Task UnwritableStorageDoesNotBreakForwarding()
    {
        var directory = NewDirectory(); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "not-a-directory"); await File.WriteAllTextAsync(file, "");
        using var store = new RequestLogStore(file);
        Assert.IsFalse(store.Available);
        Assert.IsNull(store.Begin("forward", null, null, null, null, null, null, null, null, null));
        Assert.IsNotNull(Json(store.Status).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task CapacityEvictsFinishedRecordsButPreservesActiveRecords()
    {
        using var store = new RequestLogStore(NewDirectory(), 4096);
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
        using var connection = store.Open();
        using var command = RequestLogStore.Command(connection, "SELECT bytes FROM accounting WHERE id=1");
        Assert.IsTrue((long)command.ExecuteScalar()! <= 1024 * 1024);
    }

    [TestMethod]
    public async Task SettingsPersistAndSecondWriterCannotInterruptLiveSession()
    {
        var directory = NewDirectory();
        using (var store = new RequestLogStore(directory))
        {
            var active = Begin(store);
            await store.ConfigureAsync(new RequestLogSettings(RetentionDays: 3), CancellationToken.None);
            using var second = new RequestLogStore(directory);
            Assert.IsFalse(second.Available);
            Assert.AreEqual("running", store.Detail(active.Id)!["state"]!.ToString());
        }
        using var restarted = new RequestLogStore(directory);
        Assert.AreEqual(3, restarted.Settings.RetentionDays);
    }

    [TestMethod]
    public void ReplacementGenerationCanAcquireStoreAfterOldGenerationStops()
    {
        var directory = NewDirectory();
        using var old = new RequestLogStore(directory);
        using var terminal = new UniversalForwardTerminal(ChannelKeysTests.Host(
            ChannelKeysTests.Account([new ChannelKey { Id = "key", Secret = "secret" }], 0)))
            { RequestLogs = new RequestLogStore(directory) };
        Assert.IsFalse(terminal.RequestLogs.Available);
        old.Dispose();
        typeof(UniversalForwardTerminal).GetMethod("InitializeRequestLogs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(terminal, null);
        Assert.IsTrue(terminal.RequestLogs.Available);
        Assert.AreEqual(directory, terminal.RequestLogs.DirectoryPath);
    }

    [TestMethod]
    public void NewerSchemaIsNotOverwritten()
    {
        var directory = NewDirectory();
        using (var store = new RequestLogStore(directory))
        {
            using var connection = store.Open();
            RequestLogStore.Exec(connection, "PRAGMA user_version=2");
        }
        using var reopened = new RequestLogStore(directory);
        Assert.IsFalse(reopened.Available);
        StringAssert.Contains(Json(reopened.Status).GetProperty("error").GetString()!, "版本");
    }

    [TestMethod]
    public async Task ConcurrentRequestsAreIsolatedAndFiltersDoNotLoadBodies()
    {
        using var store = new RequestLogStore(NewDirectory(), 4096);
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
}
