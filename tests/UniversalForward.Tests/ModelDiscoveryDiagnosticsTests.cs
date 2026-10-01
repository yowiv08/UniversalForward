using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Moq;
using Plugins.UniversalForward;
using Router.Contracts.Host;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ModelDiscoveryDiagnosticsTests
{
    [TestMethod]
    [DataRow(200, "<html>\nblocked by gateway\n</html>", "text/html")]
    [DataRow(200, "", "application/json")]
    [DataRow(200, "{broken", "application/json")]
    [DataRow(200, "{\"data\":[]}", "application/json")]
    [DataRow(401, "{\"error\":{\"message\":\"client denied\",\"detail\":\"full detail\"}}", "application/json")]
    [DataRow(429, "rate limited\ntry later", "text/plain")]
    public async Task FailuresPreserveBodyAndMetadata(int status, string body, string type)
    {
        var (text, host) = await Discover(_ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, type),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://upstream.example/login")
        });
        StringAssert.Contains(text, $"HTTP: {status}");
        StringAssert.Contains(text, "请求地址: https://upstream.example/v1/models");
        StringAssert.Contains(text, "最终地址: https://upstream.example/login");
        StringAssert.Contains(text, type);
        StringAssert.Contains(text, body.Length == 0 ? "响应正文为空" : body);
        var log = Mock.Get(host.Services.Log).Invocations.Select(i => i.Arguments[0]).OfType<PluginLog>().Single();
        StringAssert.Contains(JsonSerializer.Serialize(log), JsonSerializer.Serialize(body.Length == 0 ? "响应正文为空" : body)[1..^1]);
    }

    [TestMethod]
    public async Task LongBodyIsNotSummarizedAndCredentialsAreRedacted()
    {
        var body = "{\"token\":\"unknown-token\",\"message\":\"draft-secret\"}\nCookie: session=private\n" +
            new string('x', 3000) + "evidence-after-1200\n" + new string('中', 30000);
        var (text, _) = await Discover(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        StringAssert.Contains(text, "evidence-after-1200");
        StringAssert.Contains(text, "已截断");
        Assert.IsFalse(text.Contains("draft-secret"));
        Assert.IsFalse(text.Contains("unknown-token"));
        Assert.IsFalse(text.Contains("session=private"));
        Assert.IsFalse(text.Contains('\ufffd'));
        Assert.IsTrue(Encoding.UTF8.GetByteCount(text) < 67000);
    }

    [TestMethod]
    public async Task NetworkErrorIncludesTypeAndUrl()
    {
        var (text, _) = await Discover(_ => throw new HttpRequestException("TLS failure draft-secret"));
        StringAssert.Contains(text, "HttpRequestException: TLS failure [REDACTED]");
        StringAssert.Contains(text, "HTTP: 未收到响应");
        StringAssert.Contains(text, "https://upstream.example/v1/models");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RedirectAndGzipUseDecodedBodyAndFinalAddress(bool validJson)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = validJson ? "{\"data\":[{\"id\":\"gzip-model\"}]}" : "<html>compressed gateway response</html>";
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, true))
            gzip.Write(Encoding.UTF8.GetBytes(body));
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                var headers = i == 0
                    ? $"HTTP/1.1 302 Found\r\nLocation: {origin}/landing\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Type: {(validJson ? "application/json" : "text/html")}; charset=utf-8\r\nContent-Encoding: gzip\r\nContent-Length: {compressed.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);
                if (i == 1) await stream.WriteAsync(compressed.ToArray(), timeout.Token);
            }
        }, timeout.Token);
        var host = PluginTestHost.Create("universalforward");
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All, UseProxy = false
            }));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new { baseUrl = origin, apiKey = "draft-secret" }));
        await server;
        var json = JsonSerializer.SerializeToElement(result);
        if (validJson)
        {
            Assert.AreEqual(200, result.StatusCode);
            StringAssert.Contains(json.ToString(), "gzip-model");
        }
        else
        {
            var text = FindError(json)!;
            StringAssert.Contains(text, $"请求地址: {origin}/v1/models");
            StringAssert.Contains(text, $"最终地址: {origin}/landing");
            StringAssert.Contains(text, body);
        }
    }

    private static async Task<(string Text, IPluginHost Host)> Discover(Func<HttpRequestMessage, HttpResponseMessage> send)
    {
        var host = PluginTestHost.Create("universalforward");
        using var handler = new Handler(send);
        Mock.Get(host.Services.Http).Setup(x => x.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()))
            .Returns(() => new HttpClient(handler, false));
        using var terminal = new UniversalForwardTerminal(host);
        var result = await terminal.DiscoverModelsAsync(ChannelKeysTests.Context(new
        {
            baseUrl = "https://upstream.example", apiKey = "draft-secret"
        }));
        var json = JsonSerializer.SerializeToElement(result);
        var error = FindError(json);
        Assert.IsNotNull(error);
        return (error, host);
    }

    private static string? FindError(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name == "error") return property.Value.GetString();
            if (FindError(property.Value) is { } error) return error;
        }
        return null;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
