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
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSettings(context.Account, out var settings))
            return LocalFailure("账号配置无效");
        if (!IsAccountEligible(context.Account, context.Request))
            return LocalFailure("账号未启用此模型或端点");
        if (!TryValidateBaseUrl(settings.BaseUrl, out var urlError)) return LocalFailure(urlError);
        if (!TryReadReplaceHeaders(ReadExtraParams(settings), out var headers, out var headerError))
            return LocalFailure(headerError);
        byte[] payload;
        var upstreamEndpoint = context.Request.Endpoint;
        try
        {
            settings.RequestPolicy.Validate();
            var model = UpstreamModel(context.Request.Model);
            if (settings.ModelProtocols.TryGetValue(model, out var protocol))
            {
                upstreamEndpoint = protocol.Select(context.Request.Endpoint);
            }
            if (context.Request.OriginalBody is not { ValueKind: JsonValueKind.Object } original)
                return LocalFailure("请求体必须为 JSON 对象");
            var body = JsonNode.Parse(original.GetRawText())!.AsObject();
            body["model"] = UpstreamModel(context.Request.Model);
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
        var watch = Stopwatch.StartNew();
        try
        {
            var isTest = context.HttpClient is ConnectionTestClient;
            var resolvedHeaders = HeaderOverrides.Resolve(settings.HeaderOverride, context.Request.RequestHeaders,
                settings.ApiKey, isTest, ClientProfiles.Variables(
                    isTest ? null : JsonNode.Parse(context.Request.OriginalBody!.Value.GetRawText()),
                    isTest ? null : context.Request.RequestHeaders));
            var profile = ClientProfiles.Profile(settings.HeaderOverride);
            if (isTest && (profile == "codex" && upstreamEndpoint == "/v1/responses"
                || profile == "claude" && upstreamEndpoint == "/v1/messages"))
            {
                resolvedHeaders["Accept"] = "text/event-stream";
                var testModel = UpstreamModel(context.Request.Model);
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
                var body = ClientProfiles.PrepareClaudeRequest(JsonNode.Parse(payload)!.AsObject(), resolvedHeaders);
                payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            }
            var targetEndpoint = profile == "claude" && upstreamEndpoint == "/v1/messages"
                ? upstreamEndpoint + "?beta=true" : upstreamEndpoint;
            for (var attempt = 0; attempt <= policy.MaxRetries; attempt++)
            {
                total.Token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(settings.BaseUrl, targetEndpoint))
                { Content = new ByteArrayContent(payload) };
                request.Content.Headers.ContentType = new("application/json");
                ApplyRequestHeaders(request, context.Request, settings, ReadExtraParams(settings), headers, upstreamEndpoint);
                ApplyReplaceHeaders(request, resolvedHeaders);
                using var headerBudget = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                headerBudget.CancelAfter(TimeSpan.FromSeconds(policy.HeaderTimeoutSeconds));
                try
                {
                    response = await context.HttpClient.SendAsync(request, false,
                        HttpCompletionOption.ResponseHeadersRead, headerBudget.Token);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                {
                    total.Token.ThrowIfCancellationRequested();
                    await TryLogAsync("request.transport.failed", error.Message, "Error", context.TraceId,
                        context.Account.Id, context.Request.Model, details: new { keyId = selectedKey.Id, keyName = selectedKey.Name, strategy = settings.KeySelectionMode, attempt, retry = attempt < policy.MaxRetries });
                    if (attempt < policy.MaxRetries) continue;
                    return Completed(RawResponse(error is OperationCanceledException ? 504 : 502,
                        JsonSerializer.SerializeToUtf8Bytes(new { error = error.Message }), "application/json"), 0);
                }
                var originalCode = (int)response.StatusCode;
                if (rules.Contains(originalCode) && attempt < policy.MaxRetries)
                {
                    response.Dispose(); response = null;
                    await TryLogAsync("request.retry", $"HTTP {originalCode}，重试同一上游", traceId: context.TraceId,
                        accountId: context.Account.Id, model: context.Request.Model, statusCode: originalCode,
                        details: new { keyId = selectedKey.Id, keyName = selectedKey.Name, strategy = settings.KeySelectionMode, attempt, retryNumber = attempt + 1 });
                    continue;
                }
                var mappedCode = policy.Map(originalCode);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                await TryLogAsync("request.response", $"HTTP {originalCode} → {mappedCode}", traceId: context.TraceId,
                    accountId: context.Account.Id, model: context.Request.Model, statusCode: originalCode,
                    durationMs: (int)watch.ElapsedMilliseconds, details: new { keyId = selectedKey.Id, keyName = selectedKey.Name, strategy = settings.KeySelectionMode, originalCode, mappedCode, retries = attempt,
                        incomingEndpoint = context.Request.Endpoint, upstreamEndpoint });
                if (response.IsSuccessStatusCode && (context.Request.Stream || contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)))
                {
                    var lifetime = new ForwardResponseLifetime(response, total);
                    var result = new AdapterResponse
                    {
                        StatusCode = mappedCode, ContentType = contentType, IsStreaming = true,
                        IsRawPassthrough = true, Lifetime = lifetime,
                        RawStream = ReadBudgetedStreamAsync(lifetime, policy.StreamIdleTimeoutSeconds)
                    };
                    transferred = true; response = null;
                    return Completed(result, originalCode);
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(total.Token);
                var output = new AdapterResponse
                {
                    StatusCode = mappedCode, RawContent = bytes, ContentType = contentType,
                    IsRawPassthrough = true,
                    Usage = response.IsSuccessStatusCode ? TryReadUsage(bytes) : null
                };
                if (!response.IsSuccessStatusCode)
                    await TryLogAsync("request.upstream.failed", Encoding.UTF8.GetString(bytes), "Error",
                        context.TraceId, context.Account.Id, context.Request.Model, originalCode);
                return Completed(output, originalCode);
            }
            throw new InvalidOperationException("重试预算无效");
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            return Completed(RawResponse(504, JsonSerializer.SerializeToUtf8Bytes(new { error = "请求总时限已耗尽" }), "application/json"), 0);
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
        { return LocalFailure(error.Message); }
        finally
        {
            response?.Dispose();
            if (!transferred) total.Dispose();
        }
    }

    private static PluginInvocationResult LocalFailure(string error)
        => Completed(AdapterResponse.BadRequest(error), 400);

    private static PluginInvocationResult Completed(AdapterResponse response, int originalCode)
        => new(response, new PluginAttemptDecision
        {
            FailureKind = originalCode is >= 200 and < 300
                ? PluginFailureKind.None : PluginFailureKind.Upstream,
            Retry = PluginRetryAction.None, AccountAction = PluginAccountAction.None,
            ProxyAction = PluginProxyAction.None
        }.ToResult(originalCode == 0 ? null : originalCode));

    private static string UpstreamModel(string model)
        => model.StartsWith(ForwardApiPlatform + "/", StringComparison.Ordinal)
            ? model[(ForwardApiPlatform.Length + 1)..] : model;
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadBudgetedStreamAsync(
        ForwardResponseLifetime lifetime, int idleSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using (lifetime)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken))
        {
            var stream = await lifetime.Response.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new byte[16384];
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
                var count = await stream.ReadAsync(buffer, idle.Token);
                if (count == 0) yield break;
                yield return buffer.AsMemory(0, count).ToArray();
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
        return safe.Length <= 1200 ? safe : safe[..1200];
    }
    private static bool IsSafeCustomHeader(string name)
        => ForwardHeaders.SafeName(name);
    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        AdapterRequest source,
        ForwardApiSettings settings,
        JsonObject extra,
        IReadOnlyDictionary<string, string> replaceHeaders,
        string upstreamEndpoint)
    {
        ForwardHeaders.ApplySelected(request, source.RequestHeaders, extra);

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
