using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    [PluginEndpoint("POST", "tests/start")]
    public async Task<PluginResult> StartConnectionTestsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<ConnectionTestInput>(context.Body, out var input))
            return context.BadRequest("测试参数无效");
        if (string.IsNullOrWhiteSpace(input.AccountId))
            return context.BadRequest("渠道 ID 不能为空");
        var account = await _host.Accounts.GetAsync(input.AccountId, context.CancellationToken);
        if (account is null) return context.Json(404, new { error = "渠道不存在" });
        if (!TryReadSettings(account, out var settings)) return context.BadRequest("渠道配置无效");
        if (!settings.Enabled || account.Status.State is ResourceState.Disabled or ResourceState.Invalid)
            return context.BadRequest("渠道已停用，请先启用");
        var models = input.Models;
        if (models is null || models.Length is < 1 or > 100
            || models.Any(x => !ReadModels(account).Contains(x, StringComparer.Ordinal)))
            return context.BadRequest("请选择该渠道的 1–100 个允许模型");
        var endpoint = string.IsNullOrWhiteSpace(input.Endpoint) ? null : input.Endpoint;
        if (endpoint is not null && !settings.Endpoints.Contains(endpoint))
            return context.BadRequest("请选择渠道允许的端点");
        foreach (var model in models)
        {
            var selected = TestEndpoint(settings, model, endpoint);
            if (!settings.Endpoints.Contains(selected)) return context.BadRequest("模型首选协议未在渠道端点中启用");
            if (settings.ModelProtocols.TryGetValue(model, out var options)
                && !options.Protocols.Any(p => ModelProtocolOptions.Endpoint(p) == selected))
                return context.BadRequest("测试端点必须属于该模型支持的协议");
        }
        var enabledKeys = ReadKeys(settings).Where(k => k.Enabled).ToArray();
        if (enabledKeys.Length == 0) return context.BadRequest("渠道没有启用的 Key");
        if (input.KeyMode is not ("strategy" or "specified" or "all")) return context.BadRequest("测试 Key 模式无效");
        if (input.KeyMode == "specified" && !enabledKeys.Any(k => k.Id == input.KeyId))
            return context.BadRequest("请选择此渠道启用的 Key");
        var keyIds = input.KeyMode switch
        {
            "all" => enabledKeys.Select(k => (string?)k.Id).ToArray(),
            "specified" => [input.KeyId],
            _ => new string?[] { null }
        };
        var uniqueModels = models.Distinct(StringComparer.Ordinal).ToArray();
        if (uniqueModels.Length * keyIds.Length > 100)
            return context.BadRequest("模型 × Key 最多 100 个组合，请减少选择");
        var normalized = new ConnectionTestInput
        {
            AccountId = account.Id, Models = uniqueModels, KeyMode = input.KeyMode, KeyIds = keyIds,
            Endpoint = endpoint, Stream = input.Stream
        };
        var job = await _host.Jobs.StartAsync("connection-test", JsonSerializer.SerializeToElement(normalized, JsonOptions),
            new PluginJobOptions { Key = account.Id, Platform = ForwardApiPlatform }, context.CancellationToken);
        return context.Json(202, job);
    }

    [PluginEndpoint("GET", "tests/status")]
    public Task<PluginResult> ConnectionTestStatusAsync(PluginHttpContext context)
    {
        var id = context.Query.GetValueOrDefault("id");
        var job = string.IsNullOrWhiteSpace(id) ? null : _host.Jobs.Get(id);
        return Task.FromResult(job is null || job.Name != "connection-test" || job.Platform != ForwardApiPlatform
            ? context.Json(404, new { error = "测试不存在" }) : context.Ok(job));
    }

    [PluginEndpoint("POST", "tests/cancel")]
    public async Task<PluginResult> CancelConnectionTestsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<AccountIdInput>(context.Body, out var input) || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("测试 ID 不能为空");
        var job = _host.Jobs.Get(input.Id);
        if (job is null || job.Name != "connection-test" || job.Platform != ForwardApiPlatform)
            return context.Json(404, new { error = "测试不存在" });
        var accepted = await _host.Jobs.CancelAsync(input.Id, context.CancellationToken);
        return context.Ok(new { accepted });
    }
    private async Task<JsonElement?> RunConnectionTestsAsync(PluginJobContext job)
    {
        var input = job.Input?.Deserialize<ConnectionTestInput>(JsonOptions)
            ?? throw new InvalidOperationException("缺少测试参数");
        var rows = new List<object>();
        var keyIds = input.KeyIds ?? [null];
        if (keyIds.Length == 0 || input.Models.Length * keyIds.Length > 100)
            throw new InvalidOperationException("模型 × Key 最多 100 个组合");
        foreach (var model in input.Models)
        foreach (var requestedKeyId in keyIds)
        {
            job.CancellationToken.ThrowIfCancellationRequested();
            job.ReportProgress(JsonSerializer.SerializeToElement(new
            { completed = rows.Count, total = input.Models.Length * keyIds.Length, running = model, runningKeyId = requestedKeyId, rows }, JsonOptions));
            var watch = Stopwatch.StartNew();
            long? firstEventMs = null;
            var sends = 0;
            var keyId = requestedKeyId;
            string? keyName = null;
            ConnectionTestClient? captureClient = null;
            try
            {
                var account = await _host.Accounts.GetAsync(input.AccountId, job.CancellationToken)
                    ?? throw new InvalidOperationException("渠道已删除");
                if (!TryReadSettings(account, out var settings)) throw new InvalidOperationException("渠道配置无效");
                var selectedKey = _keySelector.Select(account.Id, ReadKeys(settings), settings.KeySelectionMode, requestedKeyId);
                keyId = selectedKey.Id; keyName = selectedKey.Name;
                var endpoint = TestEndpoint(settings, model, input.Endpoint);
                using var http = _host.Http.CreateDirectClient(new PluginHttpClientOptions { AllowAutoRedirect = false });
                http.Timeout = Timeout.InfiniteTimeSpan;
                using var client = new ConnectionTestClient(http, () => sends++, keyId);
                captureClient = client;
                var body = CreateTestBody(model, endpoint, input.Stream);
                var result = await InvokeAsync(new PluginAttemptContext
                {
                    PluginKey = ForwardApiPlatform, PlatformName = ForwardApiPlatform, Account = account,
                    HttpClient = client, CancellationToken = job.CancellationToken, TraceId = job.Id,
                    Request = new AdapterRequest
                    {
                        Model = model, Endpoint = endpoint, Stream = input.Stream, OriginalBody = body
                    }
                });
                keyId = client.SelectedKey?.Id ?? requestedKeyId;
                keyName = client.SelectedKey?.Name ?? keyName;
                var valid = false;
                string? error = null;
                try
                {
                    var responseStream = result.Response.RawStream;
                    if (responseStream is null && result.Response.RawContent is { } cached
                        && result.Response.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
                        && !IsJsonError(cached))
                        responseStream = CachedTestStream(cached);
                    if (responseStream is { } stream)
                    {
                        var decoder = new UTF8Encoding(false, true).GetDecoder();
                        var pending = new StringBuilder();
                        var terminal = false;
                        var protocolError = false;
                        var recognized = false;
                        await foreach (var bytes in stream.WithCancellation(job.CancellationToken))
                        {
                            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                            var count = decoder.GetChars(bytes.Span, chars, false);
                            pending.Append(chars, 0, count);
                            if (pending.Length > 1024 * 1024) throw new InvalidOperationException("SSE 事件超过 1 MiB");
                            var text = pending.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
                            int end;
                            while ((end = text.IndexOf("\n\n", StringComparison.Ordinal)) >= 0)
                            {
                                var frame = text[..end]; text = text[(end + 2)..];
                                var data = string.Join("\n", frame.Split('\n')
                                    .Where(x => x.StartsWith("data:", StringComparison.Ordinal)).Select(x => x[5..].TrimStart()));
                                if (data.Length == 0) continue;
                                if (data == "[DONE]")
                                {
                                    continue;
                                }
                                using var doc = JsonDocument.Parse(data);
                                var root = doc.RootElement;
                                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("SSE 内容不是对象");
                                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                                var validEvent = endpoint switch
                                {
                                    "/v1/messages" => type is "message_start" or "content_block_start" or "content_block_delta",
                                    "/v1/responses" => type is "response.created" or "response.output_item.added" or "response.output_text.delta",
                                    _ => false
                                };
                                recognized |= validEvent;
                                if (validEvent) firstEventMs ??= watch.ElapsedMilliseconds;
                                terminal |= endpoint == "/v1/messages" && type == "message_stop"
                                    || endpoint == "/v1/responses" && type == "response.completed";
                                protocolError |= root.TryGetProperty("error", out var upstreamError) && upstreamError.ValueKind != JsonValueKind.Null
                                    || type is "error" or "response.failed" or "response.incomplete";
                                error ??= ReadStreamError(root);
                            }
                            pending.Clear().Append(text);
                        }
                        decoder.GetChars([], new char[2], true);
                        valid = terminal && recognized && !protocolError && string.IsNullOrWhiteSpace(pending.ToString());
                        if (!valid) error ??= "流缺少完成事件或上游报告错误";
                    }
                    else
                    {
                        valid = IsTestCompletion(result.Response.RawContent, endpoint);
                        if (!valid)
                        {
                            if (result.Response.RawContent is { Length: > 0 } bytes)
                            {
                                try
                                {
                                    using var document = JsonDocument.Parse(bytes);
                                    error = ReadStreamError(document.RootElement);
                                }
                                catch (JsonException) { }
                            }
                            error ??= "上游响应未正常完成，请查看原始响应";
                        }
                    }
                }
                finally { if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync(); }
                var status = result.Attempt.StatusCode ?? result.Response.StatusCode;
                rows.Add(new
                {
                    model, keyId, keyName, endpoint, success = status is >= 200 and < 300 && valid,
                    originalStatus = status, mappedStatus = result.Response.StatusCode,
                    durationMs = watch.ElapsedMilliseconds, firstEventMs, retries = Math.Max(0, sends - 1), error,
                    response = client.RawBody, responseTruncated = client.Truncated
                });
            }
            catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                rows.Add(new { model, keyId, keyName, success = false, durationMs = watch.ElapsedMilliseconds,
                    firstEventMs, retries = Math.Max(0, sends - 1), error = error.Message,
                    response = captureClient?.RawBody, responseTruncated = captureClient?.Truncated ?? false });
            }
            job.ReportProgress(JsonSerializer.SerializeToElement(new
            { completed = rows.Count, total = input.Models.Length * keyIds.Length, rows }, JsonOptions));
        }
        return JsonSerializer.SerializeToElement(new { rows }, JsonOptions);
    }

    private static string TestEndpoint(ForwardApiSettings settings, string model, string? requested)
        => requested ?? (settings.ModelProtocols.TryGetValue(model, out var options)
            ? ModelProtocolOptions.Endpoint(options.PreferredProtocol)
            : settings.Endpoints.Contains("/v1/responses") ? "/v1/responses"
            : settings.Endpoints.FirstOrDefault() ?? throw new FormatException("渠道没有允许端点"));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> CachedTestStream(byte[] bytes)
    {
        await Task.CompletedTask;
        for (var offset = 0; offset < bytes.Length; offset += 16384)
            yield return bytes.AsMemory(offset, Math.Min(16384, bytes.Length - offset));
    }

    internal static JsonElement CreateTestBody(string model, string endpoint, bool stream)
        => JsonSerializer.SerializeToElement(endpoint switch
        {
            "/v1/responses" => (object)new { model, stream, input = "Reply with OK.", max_output_tokens = 32 },
            "/v1/messages" => new { model, stream, messages = new[] { new { role = "user", content = "Reply with OK." } }, max_tokens = 32 },
            _ => throw new FormatException("仅支持 Responses 和 Anthropic Messages。")
        });

    internal static bool IsTestCompletion(byte[]? bytes, string endpoint)
    {
        if (bytes is null) return false;
        if (!SupportedEndpoints.Contains(endpoint)) return false;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) return false;
            if (endpoint == "/v1/responses"
                && (!root.TryGetProperty("status", out var status) || status.GetString() != "completed"))
                return false;
            var property = endpoint == "/v1/messages" ? "content" : "output";
            if (!root.TryGetProperty(property, out var content) || content.ValueKind != JsonValueKind.Array
                || content.GetArrayLength() == 0) return false;
            return content.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && (endpoint switch
            {
                "/v1/messages" => item.TryGetProperty("type", out var type) && type.GetString() is "text" or "tool_use",
                "/v1/responses" => item.TryGetProperty("type", out var type) && type.GetString() is "message" or "function_call",
                _ => false
            }));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    internal static string? ReadStreamError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            return ErrorText(error);
        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty("error", out error) && error.ValueKind != JsonValueKind.Null)
                return ErrorText(error);
            if (response.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reason", out var reason))
                return "上游响应未完成：" + reason.ToString();
        }
        if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() == "error")
            return ErrorText(root);
        return null;
    }

    private static string ErrorText(JsonElement error)
    {
        var text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
            ? message.ToString() : error.ToString();
        return text[..Math.Min(text.Length, 2048)];
    }

    private sealed class ConnectionTestInput
    {
        public string AccountId { get; init; } = "";
        public string[] Models { get; init; } = [];
        public string? Endpoint { get; init; }
        public bool Stream { get; init; }
        public string KeyMode { get; init; } = "strategy";
        public string? KeyId { get; init; }
        public string?[]? KeyIds { get; init; }
    }

    private sealed class ConnectionTestClient(HttpClient client, Action sent, string? keyId) : IPluginHttpClient, IDisposable
    {
        private readonly MemoryStream _capture = new();
        public string RawBody => Encoding.UTF8.GetString(_capture.ToArray());
        public bool Truncated { get; private set; }
        public void Dispose() => _capture.Dispose();
        public string? KeyId { get; } = keyId;
        public ChannelKey? SelectedKey { get; set; }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
            => SendAsync(request, false, option, ct);
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool useProxyPool, HttpCompletionOption option, CancellationToken ct)
        {
            if (useProxyPool) throw new InvalidOperationException("渠道测试只使用直连");
            sent();
            _capture.SetLength(0);
            Truncated = false;
            var response = await client.SendAsync(request, option, ct);
            var original = response.Content;
            try
            {
                var stream = await original.ReadAsStreamAsync(ct);
                var content = new StreamContent(new CaptureStream(stream, original, bytes =>
                {
                    var remaining = 32 * 1024 * 1024 - (int)_capture.Length;
                    _capture.Write(bytes.Span[..Math.Min(bytes.Length, remaining)]);
                    Truncated |= bytes.Length > remaining;
                }));
                foreach (var header in original.Headers)
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = content;
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private sealed class CaptureStream(Stream source, HttpContent owner, Action<ReadOnlyMemory<byte>> capture) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = source.Read(buffer, offset, count);
            capture(buffer.AsMemory(offset, n));
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await source.ReadAsync(buffer, cancellationToken);
            capture(buffer[..n]);
            return n;
        }
        protected override void Dispose(bool disposing) { if (disposing) owner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
