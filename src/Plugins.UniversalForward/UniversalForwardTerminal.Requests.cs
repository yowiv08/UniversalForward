using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Plugins.UniversalForward;


public sealed partial class UniversalForwardTerminal
{
    /// <summary>执行上游请求与重试。</summary>
    public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
    {
        TryReadSettings(context.Account, out var settings);
        var capture = BeginRequestLog(store => store.Begin(context.HttpClient is ConnectionTestClient ? "connection-test" : "forward",
            context.Account.Id, context.Account.Label, context.Request.Model, context.Request.Endpoint,
            settings.NetworkMode, context.TraceId,
            context.Request.GetType().GetProperty("DownstreamRequestHeaders")?.GetValue(context.Request) ?? context.Request.RequestHeaders,
            context.Request.OriginalBody?.GetRawText(), JsonSerializer.Serialize(context.Request.Extensions, JsonOptions)));
        try
        {
            var result = await InvokeCoreAsync(context, capture);
            return capture is null ? result : result with { Response = capture.Attach(result.Response) };
        }
        catch (Exception error)
        {
            capture?.Complete(error is OperationCanceledException ? "cancelled" : "failed", error: error.Message);
            throw;
        }
    }
    private async Task<PluginInvocationResult> InvokeCoreAsync(PluginAttemptContext context, RequestLogCapture? capture)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSettings(context.Account, out var settings))
            return LocalFailure("账号配置无效");
        if (!IsAccountEligible(context.Account, context.Request, context.HttpClient is ConnectionTestClient))
            return LocalFailure("账号未启用此模型或端点");
        if (!TryValidateBaseUrl(settings.BaseUrl, out var urlError)) return LocalFailure(urlError);
        if (!TryReadReplaceHeaders(ReadExtraParams(settings), out var headers, out var headerError))
            return LocalFailure(headerError);
        byte[] payload;
        ReasoningMappingDecision? reasoningMapping;
        var upstreamModel = ReadModels(context.Account).Contains(context.Request.Model, StringComparer.OrdinalIgnoreCase)
            ? context.Request.Model : UpstreamModel(context.Request.Model);
        var upstreamEndpoint = context.Request.Endpoint;
        try
        {
            settings.RequestPolicy.Validate();
            var model = upstreamModel;
            if (settings.ModelProtocols.TryGetValue(model, out var protocol))
            {
                upstreamEndpoint = protocol.Select(context.Request.Endpoint);
            }
            if (context.Request.OriginalBody is not { ValueKind: JsonValueKind.Object } original)
                return LocalFailure("请求体必须为 JSON 对象");
            var body = JsonNode.Parse(original.GetRawText())!.AsObject();
            body["model"] = upstreamModel;
            reasoningMapping = settings.ReasoningPolicy.Resolve(body, upstreamModel, upstreamEndpoint);
            payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        { return LocalFailure(error.Message); }

        ChannelKey selectedKey;
        try
        {
            selectedKey = _keySelector.Select(context.Account.Id, ReadKeys(settings), settings.KeySelectionMode,
                (context.HttpClient as ConnectionTestClient)?.KeyId);
            settings.ApiKey = selectedKey.Secret;
            if (context.HttpClient is ConnectionTestClient testClient) testClient.SelectedKey = selectedKey;
        }
        catch (FormatException error) { return LocalFailure(error.Message); }

        var policy = settings.RequestPolicy;
        var rules = ForwardRetryRules.Parse(policy.RetryStatusCodes);
        var total = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        total.CancelAfter(TimeSpan.FromSeconds(policy.TotalTimeoutSeconds));
        var transferred = false;
        HttpResponseMessage? response = null;
        HttpClient? networkClient = null;
        AdapterResponse? lastRateLimit = null;
        var anomalyRetries = 0;
        var inspectResponses = policy.RateLimitRetryEnabled || policy.EmptyResponseRetryEnabled;
        var watch = Stopwatch.StartNew();
        try
        {
            var isTest = context.HttpClient is ConnectionTestClient;
            var extra = ReadExtraParams(settings);
            var clientHeaders = isTest ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                : ForwardHeaders.ReadClientHeaders(context.Request, extra);
            var clientHeaderText = clientHeaders.ToDictionary(p => p.Key, p => string.Join(", ", p.Value), StringComparer.OrdinalIgnoreCase);
            var websocket = settings.ResponsesTransport == "websocket" && upstreamEndpoint == "/v1/responses";
            if ((settings.NetworkMode == "proxyPool" || websocket) && !isTest)
                networkClient = await CreateNetworkClientAsync(settings.NetworkMode, false, total.Token);
            await TryLogAsync("request.network.selected", $"出站方式: {settings.NetworkMode}",
                traceId: context.TraceId, accountId: context.Account.Id, model: context.Request.Model,
                details: new { networkMode = settings.NetworkMode });
            var headerConfig = HeaderOverrides.Select(settings.HeaderOverrideMode, settings.HeaderOverride,
                settings.EndpointHeaderOverrides, upstreamEndpoint);
            HeaderOverrides.Validate(headerConfig);
            var profile = ClientProfiles.Profile(headerConfig);
            var identityBody = isTest ? null : JsonNode.Parse(context.Request.OriginalBody!.Value.GetRawText());
            var identitySource = isTest ? null : ClientProfiles.IdentitySource(identityBody, clientHeaderText, headerConfig, headers);
            var session = !isTest && profile is not null && identitySource is null
                ? _clientSessions.Resolve(context.Account.Id, ForwardHeaders.AuthenticationDigest(context.Request),
                    identityBody, context.Request.Endpoint)
                : null;
            var variables = ClientProfiles.Variables(identityBody, isTest ? null : clientHeaderText,
                session?.Identity, headerConfig, headers);
            var resolvedHeaders = HeaderOverrides.Resolve(headerConfig, clientHeaderText, settings.ApiKey, isTest, variables, headers);
            if (profile is not null)
                await TryLogAsync("request.identity.resolved", "已解析请求会话身份",
                    traceId: context.TraceId, accountId: context.Account.Id, model: context.Request.Model,
                    details: new { source = identitySource ?? session?.Source ?? "new",
                        reason = identitySource is not null ? "provided_identity" : session?.Reason ?? "connection_test" });
            var nativeStream = websocket || profile == "codex" && upstreamEndpoint == "/v1/responses"
                || profile == "claude" && upstreamEndpoint == "/v1/messages";
            if (isTest && (profile == "codex" && upstreamEndpoint == "/v1/responses"
                || profile == "claude" && upstreamEndpoint == "/v1/messages"))
            {
                resolvedHeaders["Accept"] = "text/event-stream";
                var testModel = upstreamModel;
                var testBody = ClientProfiles.TestBody(profile, testModel, resolvedHeaders);
                payload = JsonSerializer.SerializeToUtf8Bytes(testBody, JsonOptions);
            }
            else if (profile == "codex" && upstreamEndpoint == "/v1/responses")
            {
                var body = ClientProfiles.PrepareCodexRequest(JsonNode.Parse(payload)!.AsObject(), resolvedHeaders);
                payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            }
            else if (profile == "claude" && upstreamEndpoint == "/v1/messages")
            {
                var body = ClientProfiles.PrepareClaudeRequest(JsonNode.Parse(payload)!.AsObject(), resolvedHeaders, variables["device_id"]);
                payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            }
            if (nativeStream)
            {
                var body = JsonNode.Parse(payload)!.AsObject();
                body["stream"] = true;
                payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            }
            if (reasoningMapping is not null)
            {
                var body = JsonNode.Parse(payload)!.AsObject();
                ReasoningPolicy.Apply(body, reasoningMapping);
                payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
                capture?.MappedReasoning(reasoningMapping);
            }
            if (websocket) payload = UpstreamWebSocket.CreatePayload(payload);
            var targetEndpoint = profile == "claude" && upstreamEndpoint == "/v1/messages"
                ? upstreamEndpoint + "?beta=true" : upstreamEndpoint;
            for (var attempt = 0; ;)
            {
                total.Token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(settings.BaseUrl, targetEndpoint))
                { Content = new ByteArrayContent(payload) };
                request.Content.Headers.ContentType = new("application/json");
                ApplyRequestHeaders(request, clientHeaders, settings, extra, headers, upstreamEndpoint);
                ApplyReplaceHeaders(request, resolvedHeaders);
                if (websocket) UpstreamWebSocket.PrepareHandshake(request);
                var logAttempt = capture?.Sending(request, payload, websocket ? "websocket" : "http") ?? 0;
                using var headerBudget = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                headerBudget.CancelAfter(TimeSpan.FromSeconds(policy.HeaderTimeoutSeconds));
                try
                {
                    response = websocket
                        ? context.HttpClient is ConnectionTestClient wsTest
                            ? await wsTest.SendWebSocketAsync(request, payload, capture, logAttempt, headerBudget.Token)
                            : await UpstreamWebSocket.SendAsync(networkClient!, request, payload, capture, logAttempt, headerBudget.Token)
                        : networkClient is not null
                        ? await networkClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerBudget.Token)
                        : await context.HttpClient.SendAsync(request, false,
                            HttpCompletionOption.ResponseHeadersRead, headerBudget.Token);
                    capture?.Received(logAttempt, response);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                {
                    capture?.SendError(logAttempt, error);
                    total.Token.ThrowIfCancellationRequested();
                    await TryLogAsync("request.transport.failed", error.Message, "Error", context.TraceId,
                        context.Account.Id, context.Request.Model, details: new { keyId = selectedKey.Id, keyName = selectedKey.Name, strategy = settings.KeySelectionMode, networkMode = settings.NetworkMode, attempt, retry = attempt < policy.MaxRetries });
                    if (attempt < policy.MaxRetries) { capture?.Retry(error.GetType().Name + ": " + error.Message); attempt++; continue; }
                    return Completed(RawResponse(error is OperationCanceledException ? 504 : 502,
                        JsonSerializer.SerializeToUtf8Bytes(new { error = error.Message }), "application/json"), 0,
                        settings.NetworkMode == "proxyPool" ? PluginFailureKind.Plugin : null);
                }
                var originalCode = (int)response.StatusCode;
                var skipOrdinaryRetry = false;
                byte[]? streamPrefix = null;
                if (inspectResponses || response.IsSuccessStatusCode)
                {
                    var originalContentType = response.Content.Headers.ContentType?.ToString();
                    var type = originalContentType ?? "application/json";
                    var sse = type.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
                    var probeStream = response.IsSuccessStatusCode && sse && (!nativeStream || context.Request.Stream);
                    var buffered = await BufferResponseAsync(response, policy.StreamIdleTimeoutSeconds, total.Token, probeStream);
                    if (buffered.Completed) capture?.EndPart($"attempt-{logAttempt}-response", true);
                    var bytesRead = buffered.Bytes;
                    if (buffered.Started)
                    {
                        streamPrefix = bytesRead;
                        skipOrdinaryRetry = true;
                    }
                    else
                    {
                        var blank = bytesRead.All(x => x is 9 or 10 or 13 or 32);
                        var looksJson = Encoding.UTF8.GetString(bytesRead).TrimStart().StartsWith('{');
                        var analysis = sse || looksJson || response.IsSuccessStatusCode
                            ? ForwardResponseAnalysis.Read(bytesRead, sse, buffered.Interrupted)
                            : new ForwardResponseAnalysis();
                        var limited = !analysis.PermanentError && (originalCode == 429 || analysis.RateLimited);
                        var empty = !analysis.HasError && response.IsSuccessStatusCode
                            && (blank || sse && !analysis.Terminal || buffered.Interrupted);
                        var retryKind = limited && !analysis.HasOutput && policy.RateLimitRetryEnabled ? "rate_limit"
                            : empty && !analysis.HasOutput && policy.EmptyResponseRetryEnabled ? "empty_response" : null;
                        skipOrdinaryRetry = analysis.PermanentError || retryKind != null;
                        if (limited && !analysis.HasOutput && policy.RateLimitRetryEnabled && !buffered.Interrupted)
                            lastRateLimit = RawResponse(originalCode, bytesRead, originalContentType);
                        total.Token.ThrowIfCancellationRequested();
                        if (retryKind != null)
                        {
                            var delay = ResponseRetryDelay(response, policy.ResponseRetryIntervalSeconds, limited);
                            var remaining = TimeSpan.FromSeconds(policy.TotalTimeoutSeconds) - watch.Elapsed;
                            var retry = anomalyRetries < policy.ResponseMaxRetries && delay < remaining;
                            await TryLogAsync("request.response.retry", retryKind, traceId: context.TraceId,
                                accountId: context.Account.Id, model: context.Request.Model, statusCode: originalCode,
                                details: new { kind = retryKind, analysis.State, analysis.IncompleteReason, analysis.HasOutput, analysis.HasUsage,
                                    analysis.ErrorCode, analysis.ErrorType, errorMessage = SanitizeMessage(analysis.ErrorMessage, settings),
                                    retryNumber = anomalyRetries, waitSeconds = delay.TotalSeconds,
                                    reason = retry ? "retry" : anomalyRetries >= policy.ResponseMaxRetries ? "exhausted" : "deadline" });
                            if (!retry)
                            {
                                if (limited && lastRateLimit != null)
                                {
                                    await LogAnalyzedErrorAsync(context, analysis, originalCode, lastRateLimit.StatusCode, settings);
                                    return Completed(lastRateLimit, lastRateLimit.StatusCode);
                                }
                                return ResponseAnomalyFailure(buffered.Interrupted ? "stream_interrupted"
                                    : blank ? "empty_response" : "missing_terminal");
                            }
                            anomalyRetries++;
                            capture?.Retry(retryKind);
                            response.Dispose(); response = null;
                            await Task.Delay(delay, total.Token);
                            continue;
                        }
                        if (analysis.HasError && response.IsSuccessStatusCode
                            && (analysis.ErrorPayload is not null || !analysis.HasOutput))
                        {
                            var failure = RawResponse(originalCode, bytesRead, originalContentType);
                            await LogAnalyzedErrorAsync(context, analysis, originalCode, failure.StatusCode, settings);
                            return Completed(failure, failure.StatusCode);
                        }
                        if (buffered.Interrupted || empty && analysis.HasOutput)
                            return ResponseAnomalyFailure("stream_interrupted");
                        if (empty)
                            return ResponseAnomalyFailure(blank ? "empty_response" : "missing_terminal");
                        await TryLogAsync("request.response.analyzed", analysis.State, traceId: context.TraceId,
                            accountId: context.Account.Id, model: context.Request.Model, statusCode: originalCode,
                            details: new { analysis.State, analysis.IncompleteReason, analysis.HasOutput, analysis.HasUsage,
                                analysis.ErrorCode, analysis.ErrorType, errorMessage = SanitizeMessage(analysis.ErrorMessage, settings),
                                analysis.HasError, analysis.RateLimited, retries = anomalyRetries });
                        var replacement = new ByteArrayContent(bytesRead);
                        foreach (var header in response.Content.Headers)
                            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        response.Content.Dispose();
                        response.Content = replacement;
                    }
                }
                if (!skipOrdinaryRetry && rules.Contains(originalCode) && attempt < policy.MaxRetries)
                {
                    response.Dispose(); response = null;
                    capture?.Retry($"HTTP {originalCode}");
                    await TryLogAsync("request.retry", $"HTTP {originalCode}，重试同一上游", traceId: context.TraceId,
                        accountId: context.Account.Id, model: context.Request.Model, statusCode: originalCode,
                        details: new { keyId = selectedKey.Id, keyName = selectedKey.Name, strategy = settings.KeySelectionMode, attempt, retryNumber = attempt + 1 });
                    if (originalCode == 520)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 250 * (1 << Math.Min(attempt, 3)))), total.Token);
                    attempt++;
                    continue;
                }
                var mappedCode = policy.Map(originalCode);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                await TryLogAsync("request.response", $"HTTP {originalCode} → {mappedCode}", traceId: context.TraceId,
                    accountId: context.Account.Id, model: context.Request.Model, statusCode: originalCode,
                    durationMs: (int)watch.ElapsedMilliseconds, details: new { keyId = selectedKey.Id, keyName = selectedKey.Name, strategy = settings.KeySelectionMode, originalCode, mappedCode, retries = attempt,
                        responseRetries = anomalyRetries, streamStarted = streamPrefix != null,
                        incomingEndpoint = context.Request.Endpoint, upstreamEndpoint, networkMode = settings.NetworkMode,
                        transport = websocket ? "websocket" : "http",
                        downstreamStream = context.Request.Stream, upstreamStream = nativeStream || context.Request.Stream,
                        upstreamRequestId = ResponseHeader(response, "x-request-id"), upstreamTraceId = ResponseHeader(response, "x-trace-id"),
                        gatewayRay = ResponseHeader(response, "cf-ray") });
                if (nativeStream && !context.Request.Stream && response.IsSuccessStatusCode
                    && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    await using var lifetime = new ForwardResponseLifetime(response, total, networkClient);
                    networkClient = null;
                    transferred = true; response = null;
                    var collected = await ForwardStreamCollector.CollectAsync(
                        ReadBudgetedStreamAsync(lifetime, policy.StreamIdleTimeoutSeconds, context, settings), upstreamEndpoint, context.CancellationToken);
                    return Completed(new AdapterResponse
                    {
                        StatusCode = mappedCode, RawContent = collected, ContentType = "application/json",
                        IsRawPassthrough = true, Usage = TryReadUsage(collected)
                    }, originalCode);
                }
                if (response.IsSuccessStatusCode && (context.Request.Stream || contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)))
                {
                    var lifetime = new ForwardResponseLifetime(response, total, networkClient);
                    networkClient = null;
                    var result = new AdapterResponse
                    {
                        StatusCode = mappedCode, ContentType = contentType, IsStreaming = true,
                        IsRawPassthrough = true, Lifetime = lifetime,
                        RawStream = ReadBudgetedStreamAsync(lifetime, policy.StreamIdleTimeoutSeconds, context, settings,
                            streamPrefix ?? [], contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase),
                            () => capture?.EndPart($"attempt-{logAttempt}-response", true))
                    };
                    transferred = true; response = null;
                    return Completed(result, originalCode);
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(total.Token);
                if (!response.IsSuccessStatusCode && IsJsonError(bytes))
                    contentType = "application/json";
                if (!response.IsSuccessStatusCode && bytes.Length == 0)
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { error = new
                    {
                        message = $"上游返回 HTTP {originalCode}，响应体为空",
                        type = "upstream_error", code = "empty_upstream_response", upstream_status = originalCode,
                        trace_id = context.TraceId, upstream_request_id = ResponseHeader(response, "x-request-id"),
                        upstream_trace_id = ResponseHeader(response, "x-trace-id"), gateway_ray = ResponseHeader(response, "cf-ray")
                    } });
                    contentType = "application/json";
                }
                var output = new AdapterResponse
                {
                    StatusCode = mappedCode, RawContent = bytes, ContentType = contentType,
                    IsRawPassthrough = true,
                    Usage = response.IsSuccessStatusCode ? TryReadUsage(bytes) : null
                };
                if (!response.IsSuccessStatusCode)
                    await TryLogAsync("request.upstream.failed", inspectResponses ? $"HTTP {originalCode}" : Encoding.UTF8.GetString(bytes), "Error",
                        context.TraceId, context.Account.Id, context.Request.Model, originalCode);
                return Completed(output, originalCode);
            }
        }
        catch (ProxyPoolUnavailableException)
        {
            await TryLogAsync("request.network.failed", ProxyUnavailableMessage, "Error", context.TraceId,
                context.Account.Id, context.Request.Model, 503,
                details: new { networkMode = settings.NetworkMode, code = "proxy_pool_unavailable" });
            return Completed(RawResponse(503, JsonSerializer.SerializeToUtf8Bytes(new
            { error = new { message = ProxyUnavailableMessage, code = "proxy_pool_unavailable" } }), "application/json"), 0, PluginFailureKind.Plugin);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            if (lastRateLimit != null)
            {
                await TryLogAsync("request.response.failed", "限流重试达到请求总时限", "Error", context.TraceId,
                    context.Account.Id, context.Request.Model, lastRateLimit.StatusCode,
                    details: new { reason = "total_timeout", retries = anomalyRetries });
                return Completed(lastRateLimit, lastRateLimit.StatusCode);
            }
            await TryLogAsync("request.timeout", "请求总时限已耗尽", "Error", context.TraceId,
                context.Account.Id, context.Request.Model, 504, details: new { reason = "total_timeout" });
            return Completed(RawResponse(504, JsonSerializer.SerializeToUtf8Bytes(new { error = "请求总时限已耗尽" }), "application/json"), 0);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or DecoderFallbackException or InvalidDataException)
        {
            await TryLogAsync("request.body.failed", error.Message, "Error", context.TraceId, context.Account.Id,
                context.Request.Model, 502);
            return Completed(RawResponse(502, JsonSerializer.SerializeToUtf8Bytes(new { error = new
            {
                message = error.Message, type = "upstream_error", code = "invalid_upstream_response", trace_id = context.TraceId
            } }), "application/json"), 502);
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
        { return LocalFailure(error.Message); }
        finally
        {
            response?.Dispose();
            networkClient?.Dispose();
            if (!transferred) total.Dispose();
        }
    }

    private Task LogAnalyzedErrorAsync(PluginAttemptContext context, ForwardResponseAnalysis analysis,
        int originalCode, int statusCode, ForwardApiSettings settings)
        => TryLogAsync("request.response.failed", SanitizeMessage(analysis.ErrorMessage, settings), "Error",
            context.TraceId, context.Account.Id, context.Request.Model, statusCode,
            details: new { originalCode, statusCode, analysis.ErrorCode, analysis.ErrorType, analysis.State,
                analysis.HasOutput, analysis.IncompleteReason });

    private static PluginInvocationResult ResponseAnomalyFailure(string code)
        => Completed(RawResponse(502, JsonSerializer.SerializeToUtf8Bytes(new { error = new
        {
            code, type = "upstream_error", message = code switch
            {
                "empty_response" => "上游响应为空",
                "missing_terminal" => "上游流缺少结束事件",
                _ => "上游响应流异常中断"
            }
        } }), "application/json"), 502);

    internal static TimeSpan ResponseRetryDelay(HttpResponseMessage response, int seconds, bool rateLimited)
    {
        var delay = TimeSpan.FromSeconds(seconds);
        if (!rateLimited) return delay;
        if (response.Headers.RetryAfter is { } hint)
        {
            var value = hint.Delta ?? (hint.Date - DateTimeOffset.UtcNow);
            if (value > delay) delay = value.Value;
        }
        if (response.Headers.TryGetValues("retry-after-ms", out var values)
            && double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ms)
            && double.IsFinite(ms) && ms > delay.TotalMilliseconds)
            delay = TimeSpan.FromMilliseconds(Math.Min(ms, TimeSpan.FromDays(365).TotalMilliseconds));
        return delay;
    }

    private static async Task<(byte[] Bytes, bool Interrupted, bool Started, bool Completed)> BufferResponseAsync(
        HttpResponseMessage response, int idleSeconds, CancellationToken token, bool probeStream = false)
    {
        using var output = new MemoryStream();
        using var probe = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
            ? new ForwardStreamProbe() : null;
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[16384];
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
                var count = await stream.ReadAsync(buffer, idle.Token);
                if (count == 0) return (output.ToArray(), false, false, false);
                if (output.Length + count > 32 * 1024 * 1024)
                    throw new InvalidDataException("上游响应超过 32 MiB");
                output.Write(buffer, 0, count);
                if (probe?.Observe(buffer.AsSpan(0, count), stopOnOutput: probeStream) == true)
                    return (output.ToArray(), false, true, probe.Completed);
                if (probe?.Completed == true) return (output.ToArray(), false, false, true);
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return (output.ToArray(), true, false, false);
        }
    }

    private static string? ResponseHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var value = string.Join(", ", values).ReplaceLineEndings(" ");
        return value[..Math.Min(256, value.Length)];
    }

    private static bool IsJsonError(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
        }
        catch (JsonException) { return false; }
    }

    private static PluginInvocationResult LocalFailure(string error)
        => Completed(AdapterResponse.BadRequest(error), 400);

    private static PluginInvocationResult Completed(AdapterResponse response, int originalCode, PluginFailureKind? failureKind = null)
        => new(response, new PluginAttemptDecision
        {
            FailureKind = failureKind ?? (originalCode is >= 200 and < 300
                ? PluginFailureKind.None : PluginFailureKind.Upstream),
            Retry = PluginRetryAction.None, AccountAction = PluginAccountAction.None,
            ProxyAction = PluginProxyAction.None
        }.ToResult(originalCode == 0 ? null : originalCode));

    private static string UpstreamModel(string model)
        => model.StartsWith(ForwardApiPlatform + "/", StringComparison.Ordinal)
            ? model[(ForwardApiPlatform.Length + 1)..] : model;
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadBudgetedStreamAsync(
        ForwardResponseLifetime lifetime, int idleSeconds, PluginAttemptContext context, ForwardApiSettings settings,
        ReadOnlyMemory<byte> prefix = default, bool verifySse = false,
        Action? onCompleted = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using (lifetime)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken))
        using (var probe = verifySse ? new ForwardStreamProbe() : null)
        {
            var stream = await lifetime.Response.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new byte[16384];
            linked.Token.ThrowIfCancellationRequested();
            if (!prefix.IsEmpty) yield return prefix;
            if (!prefix.IsEmpty) await ObserveAsync(prefix, false);
            // Resume once after yielding the final bytes so the writer can still
            // report a failed write. Do not wait for TCP EOF after a protocol end.
            while (probe?.Completed != true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
                int count;
                try { count = await stream.ReadAsync(buffer, idle.Token); }
                catch (Exception error) when (error is OperationCanceledException or IOException or HttpRequestException)
                {
                    var reason = context.CancellationToken.IsCancellationRequested || cancellationToken.IsCancellationRequested
                        ? "request_cancelled" : lifetime.Token.IsCancellationRequested ? "total_timeout"
                        : idle.IsCancellationRequested ? "stream_idle_timeout" : "stream_interrupted";
                    var cancellationSource = cancellationToken.IsCancellationRequested ? "downstream"
                        : context.CancellationToken.IsCancellationRequested ? "host_or_caller"
                        : lifetime.Token.IsCancellationRequested ? "plugin_total_timeout"
                        : idle.IsCancellationRequested ? "stream_idle_timeout" : "upstream";
                    var message = reason + ": " + (reason switch
                    {
                        "total_timeout" => $"请求总时限 {settings.RequestPolicy.TotalTimeoutSeconds} 秒已耗尽，尚未收到完整结束事件",
                        "stream_idle_timeout" => $"上游连续 {idleSeconds} 秒未发送数据，尚未收到完整结束事件",
                        "request_cancelled" => cancellationSource == "downstream" ? "下游已取消请求或停止读取响应" : "宿主或调用方已取消转发请求",
                        _ => "上游响应流异常中断"
                    });
                    await TryLogAsync("request.stream.failed", message, "Error", context.TraceId,
                        context.Account.Id, context.Request.Model, (int)lifetime.Response.StatusCode,
                        details: new { reason, cancellationSource, idleSeconds, totalSeconds = settings.RequestPolicy.TotalTimeoutSeconds,
                            hasOutput = probe?.Analysis.HasOutput, terminalReceived = probe?.Analysis.StreamEnded });
                    if (error is OperationCanceledException)
                    {
                        if (reason is "total_timeout" or "stream_idle_timeout") throw new TimeoutException(message, error);
                        if (reason == "request_cancelled") throw new OperationCanceledException(message, error,
                            cancellationToken.IsCancellationRequested ? cancellationToken : context.CancellationToken);
                        throw new IOException(message, error);
                    }
                    throw;
                }
                if (count == 0)
                {
                    await ObserveAsync(default, true);
                    yield break;
                }
                yield return buffer.AsMemory(0, count).ToArray();
                await ObserveAsync(buffer.AsMemory(0, count), false);
            }
            onCompleted?.Invoke();

            async Task ObserveAsync(ReadOnlyMemory<byte> bytes, bool end)
            {
                if (probe is null) return;
                try
                {
                    if (end) probe.Complete();
                    else probe.Observe(bytes.Span, stopOnOutput: false);
                    if (probe.Analysis.HasError)
                        throw new IOException("upstream_stream_error: " + SanitizeMessage(probe.Analysis.ErrorMessage, settings));
                }
                catch (Exception error) when (error is IOException or DecoderFallbackException or InvalidDataException)
                {
                    await TryLogAsync("request.stream.failed", SanitizeMessage(error.Message, settings), "Error",
                        context.TraceId, context.Account.Id, context.Request.Model, (int)lifetime.Response.StatusCode,
                        details: new { reason = end ? "missing_terminal" : "upstream_stream_error",
                            probe.Analysis.ErrorCode, probe.Analysis.ErrorType, probe.Analysis.State });
                    throw new IOException(error.Message, error);
                }
            }
        }
    }
    private async Task TryLogAsync(
        string eventType,
        string message,
        string level = "Information",
        string? traceId = null,
        string? accountId = null,
        string? model = null,
        int? statusCode = null,
        int? durationMs = null,
        string? taskName = null,
        object? details = null)
    {
        try
        {
            await _host.LogAsync(
                "universalforward",
                eventType,
                SanitizeMessage(message, null),
                level,
                traceId,
                taskName,
                accountId,
                model,
                statusCode,
                durationMs,
                details,
                CancellationToken.None);
        }
        catch
        {
        }
    }
    private static PluginInvocationResult RequestFailure(
        PluginAttemptContext context,
        AdapterResponse response,
        string reason)
        => new(
            response,
            new PluginAttemptResult(PluginAttemptOutcome.NoPenalty, response.StatusCode, Reason: reason));
    private static AdapterResponse RawResponse(int statusCode, byte[] content, string? contentType)
        => new()
        {
            StatusCode = statusCode,
            IsRawPassthrough = true,
            RawContent = content,
            ContentType = contentType
        };
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) yield break;
                yield return buffer.AsMemory(0, count).ToArray();
            }
        }
        finally
        {
            response.Dispose();
        }
    }
    private static Usage? TryReadUsage(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("usage", out var usage)) return null;
            var prompt = ReadInt(usage, "prompt_tokens") ?? ReadInt(usage, "input_tokens") ?? 0;
            var completion = ReadInt(usage, "completion_tokens") ?? ReadInt(usage, "output_tokens") ?? 0;
            var total = ReadInt(usage, "total_tokens") ?? prompt + completion;
            return new Usage(prompt, completion, total);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var number)
                ? number
                : null;
    private static string[] ParseModelIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var list = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
                ? data
                : root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("models", out var models) ? models : default;
        if (list.ValueKind != JsonValueKind.Array) return [];

        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    private static Uri BuildUri(string baseUrl, string path)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var route = path.Trim().TrimStart('/');
        if (basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            && route.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
            route = route["v1/".Length..];
        if (route.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            && basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUri = new Uri(baseUri, "../");
        return new Uri(baseUri, route);
    }
    private static JsonObject ReadExtraParams(ForwardApiSettings settings)
    {
        try { return JsonNode.Parse(settings.ExtraParams) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
    private static void ApplyApiKeyHeader(HttpRequestMessage request, ForwardApiSettings settings)
    {
        var extra = ReadExtraParams(settings);
        var keyHeader = ReadString(extra, "apiKeyHeader") ?? "Authorization";
        if (!IsSafeCustomHeader(keyHeader)) keyHeader = "Authorization";
        var prefix = ReadString(extra, "apiKeyPrefix")
            ?? (keyHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " : string.Empty);
        request.Headers.TryAddWithoutValidation(keyHeader, prefix + settings.ApiKey);
    }
    private static string? ReadString(JsonObject value, string property)
        => value[property] is JsonValue node && node.TryGetValue<string>(out var result)
            ? result
            : null;
    private static bool TryReadReplaceHeaders(
        JsonObject extra,
        out Dictionary<string, string> headers,
        out string error)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        try { ForwardHeaders.Validate(extra); }
        catch (FormatException invalid) { error = invalid.Message; return false; }
        if (!extra.TryGetPropertyValue("ReplaceHeaders", out var configured)) return true;
        if (configured is not JsonObject values)
        {
            error = "ReplaceHeaders 必须是 JSON 对象";
            return false;
        }
        foreach (var (name, node) in values)
        {
            if (!IsSafeCustomHeader(name) || node is not JsonValue value
                || !value.TryGetValue<string>(out var text) || text.Length > 8192 || text.Any(char.IsControl))
            {
                error = "ReplaceHeaders 包含不允许的请求头，或头值不是无控制字符的字符串";
                return false;
            }
            headers[name] = text;
        }
        return true;
    }
    private static string? ReadJsonString(string? json, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (var part in path)
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(part, out current))
                    return null;
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private static string DescribeResponseError(HttpStatusCode statusCode, string body, ForwardApiSettings settings)
    {
        var summary = ReadJsonString(body, "message")
            ?? ReadJsonString(body, "error", "message")
            ?? ReadJsonString(body, "error")
            ?? ReadJsonString(body, "data", "message")
            ?? body.Trim();
        if (string.IsNullOrWhiteSpace(summary)) summary = statusCode.ToString();
        return SanitizeMessage($"HTTP {(int)statusCode} {statusCode}: {summary}", settings);
    }
    private static string SanitizeMessage(string? message, ForwardApiSettings? settings)
    {
        var safe = message ?? "未知错误";
        if (settings is not null)
        {
            if (!string.IsNullOrEmpty(settings.ApiKey)) safe = safe.Replace(settings.ApiKey, "[REDACTED]", StringComparison.Ordinal);
            foreach (var key in settings.Keys ?? [])
                if (!string.IsNullOrEmpty(key.Secret)) safe = safe.Replace(key.Secret, "[REDACTED]", StringComparison.Ordinal);
        }
        return safe.Length <= 1200 ? safe : safe[..1200];
    }
    private static bool IsSafeCustomHeader(string name)
        => ForwardHeaders.SafeName(name);
    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string[]> clientHeaders,
        ForwardApiSettings settings,
        JsonObject extra,
        IReadOnlyDictionary<string, string> replaceHeaders,
        string upstreamEndpoint)
    {
        ForwardHeaders.ApplyClientHeaders(request, clientHeaders);

        var isMessages = upstreamEndpoint.Equals("/v1/messages", StringComparison.OrdinalIgnoreCase);
        var keyHeader = ReadString(extra, "apiKeyHeader")
            ?? (isMessages ? "x-api-key" : "Authorization");
        if (!IsSafeCustomHeader(keyHeader))
            keyHeader = "Authorization";
        var prefix = ReadString(extra, "apiKeyPrefix") ?? (keyHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " : string.Empty);
        request.Headers.Remove(keyHeader);
        request.Headers.TryAddWithoutValidation(keyHeader, prefix + settings.ApiKey);
        if (isMessages
            && !request.Headers.Contains("anthropic-version"))
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        ApplyReplaceHeaders(request, replaceHeaders);
    }
    private static void ApplyReplaceHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> replaceHeaders)
    {
        foreach (var (name, value) in replaceHeaders)
        {
            if (request.Headers.NonValidated.Contains(name))
                request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is { } content)
            {
                content.Headers.Remove(name);
                content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}
