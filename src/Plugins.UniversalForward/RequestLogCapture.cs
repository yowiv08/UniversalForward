using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Plugins.UniversalForward;

internal sealed class RequestLogCapture
{
    private readonly RequestLogStore _store;
    private readonly object _gate = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly JsonObject _info;
    private readonly List<JsonObject> _attempts = [];
    private readonly List<RequestTokenUsage> _tokenUsage = [];
    private readonly bool _anthropic;
    private readonly Dictionary<string, Part> _parts = new(StringComparer.Ordinal);
    private int _finished;
    private readonly int _limit;
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public RequestLogCapture(RequestLogStore store, string kind, string? channel, string? label, string? model,
        string? endpoint, string? network, string? trace, object? headers)
    {
        _store = store; _limit = store.Settings.BodyLimitBytes;
        _anthropic = endpoint?.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) == true;
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _info = new JsonObject
        {
            ["id"] = Id, ["started"] = started, ["kind"] = kind, ["channel"] = channel,
            ["channelLabel"] = label, ["model"] = model, ["requestedModel"] = model, ["endpoint"] = endpoint,
            ["network"] = network, ["trace"] = trace, ["state"] = "running", ["retries"] = 0, ["internalSends"] = 0,
            ["tokenUsage"] = new RequestTokenUsage(_anthropic).ToJson()
        };
        var json = _info.ToJsonString();
        _store.EnqueueInsert(Id, json);
        AddText("incoming-headers", JsonSerializer.Serialize(headers, RequestLogStore.Json));
    }
    private sealed class Part
    {
        public long Observed;
        public long Accepted;
        public bool Ended;
    }
    public void AddText(string name, string text)
    {
        if (RequestLogStore.IsBodyPart(name)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        Observe(name, bytes); EndPart(name, true);
        lock (_gate)
        {
            if (name == "extensions") _info["extensionReasoning"] = Reasoning(text);
        }
    }
    public void RequestMetadata(JsonElement? body)
    {
        lock (_gate)
        {
            var reason = new JsonObject();
            if (body is { ValueKind: JsonValueKind.Object } root)
            {
                foreach (var (name, keys) in ReasoningPaths)
                {
                    var value = root;
                    foreach (var key in keys)
                    {
                        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child))
                            value = child;
                        else { value = default; break; }
                    }
                    if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        reason[name] = JsonNode.Parse(value.GetRawText());
                }
                if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(model.GetString())) _info["requestedModel"] = model.GetString();
            }
            _info["receivedReasoning"] = reason;
        }
    }
    public void SelectedProxy(ProxyPoolNode? proxy)
    {
        lock (_gate)
        {
            _info["proxy"] = JsonSerializer.SerializeToNode(proxy, RequestLogStore.Json);
            Save();
        }
    }
    public void MappedReasoning(ReasoningMappingDecision decision)
    {
        lock (_gate)
        {
            _info["reasoningMapping"] = JsonSerializer.SerializeToNode(decision, RequestLogStore.Json);
            Save();
        }
    }
    public void RewroteSystemPrompt(SystemPromptRewriteDecision decision)
    {
        lock (_gate)
        {
            _info["systemPromptRewrite"] = JsonSerializer.SerializeToNode(decision, RequestLogStore.Json);
            Save();
        }
    }
    public void Observe(string name, ReadOnlySpan<byte> bytes)
    {
        if (RequestLogStore.IsBodyPart(name)) return;
        lock (_gate)
        {
            if (!_parts.TryGetValue(name, out var part)) _parts[name] = part = new Part();
            part.Observed += bytes.Length;
            var remaining = (int)Math.Min(bytes.Length, Math.Max(0, _limit - part.Accepted));
            for (var offset = 0; offset < remaining; offset += 65536)
            {
                var chunk = bytes.Slice(offset, Math.Min(65536, remaining - offset)).ToArray();
                var observed = part.Observed;
                if (_store.Enqueue(Id, c => c.AppendChunk(Id, name, observed, chunk))) part.Accepted += chunk.Length;
                else _info["incomplete"] = true;
            }
        }
    }
    public void EndPart(string name, bool eof)
    {
        if (RequestLogStore.IsBodyPart(name)) return;
        lock (_gate)
        {
            if (!_parts.TryGetValue(name, out var part)) _parts[name] = part = new Part();
            if (part.Ended) return;
            part.Ended = true;
            var observed = part.Observed; var truncated = observed > part.Accepted || !eof;
            if (truncated) _info["incomplete"] = true;
            _store.Enqueue(Id, c => c.EndPart(Id, name, observed, truncated));
        }
    }
    private static Dictionary<string, string[]> RequestHeaders(HttpRequestMessage request)
    {
        var headers = request.Headers.NonValidated.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
            foreach (var p in request.Content.Headers.NonValidated) headers[p.Key] = p.Value.ToArray();
        return headers;
    }
    public int Sending(HttpRequestMessage request, ReadOnlySpan<byte> body, string transport = "http")
    {
        lock (_gate)
        {
            var number = _attempts.Count + 1;
            var node = Parse(body);
            var path = request.RequestUri?.AbsolutePath;
            var anthropic = path?.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) == true ? true
                : path?.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) == true ? false : _anthropic;
            var tokens = new RequestTokenUsage(anthropic);
            var info = new JsonObject
            {
                ["number"] = number, ["method"] = request.Method.Method, ["url"] = request.RequestUri?.ToString(),
                ["transport"] = transport,
                ["proxy"] = _info["proxy"]?.DeepClone(),
                ["startedMs"] = _watch.ElapsedMilliseconds, ["sentReasoning"] = Reasoning(node),
                ["sentModel"] = Model(node), ["tokenUsage"] = tokens.ToJson()
            };
            if (transport == "websocket") info["url"] = UpstreamWebSocket.Address(request.RequestUri!).ToString();
            _attempts.Add(info);
            _tokenUsage.Add(tokens);
            _info["tokenUsage"] = tokens.ToJson();
            _info["retries"] = number - 1;
            _info["sentReasoning"] = info["sentReasoning"]?.DeepClone();
            _info["sentModel"] = info["sentModel"]?.DeepClone();
            _info.Remove("reportedModel");
            _info.Remove("reportedReasoning");
            _info["reasoningChanged"] = !JsonNode.DeepEquals(_info["receivedReasoning"], _info["sentReasoning"]);
            if (transport != "websocket")
                AddText($"attempt-{number}-request-headers", JsonSerializer.Serialize(RequestHeaders(request), RequestLogStore.Json));
            SaveAttempt(number); Save();
            return number;
        }
    }
    public void WebSocketHandshake(int number, HttpRequestMessage request)
    {
        AddText($"attempt-{number}-request-headers", JsonSerializer.Serialize(RequestHeaders(request), RequestLogStore.Json));
    }
    public void WebSocketHandshakeResponse(int number, HttpResponseMessage response)
    {
        lock (_gate) RecordResponse(number, response, websocket: true);
    }
    public void Received(int number, HttpResponseMessage response)
    {
        lock (_gate)
        {
            if (_attempts[number - 1]["transport"]?.ToString() != "websocket")
                RecordResponse(number, response);
            response.Content = new CapturedContent(response.Content, this, number);
        }
    }
    private void RecordResponse(int number, HttpResponseMessage response, bool websocket = false)
    {
        var info = _attempts[number - 1];
        info["status"] = (int)response.StatusCode;
        info["headersMs"] = _watch.ElapsedMilliseconds;
        var uri = response.RequestMessage?.RequestUri;
        info["finalUrl"] = uri is null ? info["url"]?.ToString()
            : websocket ? UpstreamWebSocket.Address(uri).ToString() : uri.ToString();
        _info["upstreamStatus"] = (int)response.StatusCode;
        var headers = response.Headers.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var p in response.Content.Headers) headers[p.Key] = p.Value.ToArray();
        AddText($"attempt-{number}-response-headers", JsonSerializer.Serialize(headers, RequestLogStore.Json));
        SaveAttempt(number); Save();
    }
    public void SendError(int number, Exception error)
    {
        lock (_gate)
        {
            _attempts[number - 1]["error"] = $"{error.GetType().Name}: {error.Message}";
            SaveAttempt(number);
        }
    }
    public void Retry(string reason)
    {
        lock (_gate)
        {
            if (_attempts.Count == 0) return;
            _attempts[^1]["retryReason"] = reason; SaveAttempt(_attempts.Count);
        }
    }
    public void HostDecision(int internalSends, bool continuationAllowed, string reason)
    {
        lock (_gate)
        {
            _info["internalSends"] = internalSends;
            _info["hostContinuationAllowed"] = continuationAllowed;
            _info["hostContinuationReason"] = reason;
            Save();
        }
    }
    private void SaveAttempt(int number)
    {
        var json = _attempts[number - 1].ToJsonString();
        _store.Enqueue(Id, c => c.Attempt(Id, number, json));
    }
    public void Save()
    {
        lock (_gate)
        {
            var json = _info.ToJsonString();
            _store.Enqueue(Id, c => c.Update(Id, json));
        }
    }
    public void Complete(string state, int? status = null, string? error = null)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        lock (_gate)
        {
            foreach (var name in _parts.Keys.ToArray()) EndPart(name, false);
            if (error is not null) AddText("error", error);
            _info["state"] = state; _info["status"] = status; _info["errorPresent"] = error is not null;
            _info["durationMs"] = _watch.ElapsedMilliseconds;
            Save(); _store.Enqueue(Id, c => c.Cleanup());
        }
    }
    private void CompleteInterrupted(string state, int status, string? error = null)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _finished) != 0) return;
            if (_info["firstByteMs"] is not null) _info["incomplete"] = true;
            Complete(state, status, error);
        }
    }
    private static readonly (string Name, string[] Keys)[] ReasoningPaths =
    [
        ("reasoning_effort", ["reasoning_effort"]), ("effort", ["effort"]),
        ("reasoning.effort", ["reasoning", "effort"]), ("thinking.type", ["thinking", "type"]),
        ("thinking.effort", ["thinking", "effort"]), ("thinking.budget_tokens", ["thinking", "budget_tokens"]),
        ("output_config.effort", ["output_config", "effort"]),
        ("usage.output_tokens_details.reasoning_tokens", ["usage", "output_tokens_details", "reasoning_tokens"]),
        ("usage.completion_tokens_details.reasoning_tokens", ["usage", "completion_tokens_details", "reasoning_tokens"])
    ];
    public static JsonObject Reasoning(string text) => Reasoning(Parse(text));
    private static JsonObject Reasoning(JsonNode? root)
    {
        var result = new JsonObject();
        if (root is not JsonObject) return result;
        foreach (var (name, keys) in ReasoningPaths)
        {
            JsonNode? value = root;
            foreach (var key in keys) value = value is JsonObject obj ? obj[key] : null;
            if (value is JsonValue) result[name] = value.DeepClone();
        }
        return result;
    }
    private static bool MergeReasoning(JsonObject target, JsonObject report)
    {
        if (report.Count == 0) return false;
        var values = target["reportedReasoning"] as JsonObject;
        var changed = false;
        foreach (var (name, value) in report)
        {
            if (values is not null && JsonNode.DeepEquals(values[name], value)) continue;
            if (values is null) { values = new JsonObject(); target["reportedReasoning"] = values; }
            values[name] = value?.DeepClone(); changed = true;
        }
        return changed;
    }
    private static string? Model(JsonNode? node)
        => node is JsonObject obj && obj["model"] is JsonValue value
            && value.TryGetValue<string>(out var model) && !string.IsNullOrWhiteSpace(model) ? model : null;

    private static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }
    private static JsonNode? Parse(ReadOnlySpan<byte> bytes)
    {
        try { return JsonNode.Parse(bytes); }
        catch (JsonException) { return null; }
    }

    private void Report(int number, string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            var payload = node;
            if (node is JsonObject obj)
            {
                if (obj["response"] is JsonObject response) payload = response;
                else if (obj["message"] is JsonObject message) payload = message;
            }
            var model = Model(payload) ?? Model(node);
            var report = Reasoning(payload);
            JsonNode? usage = null;
            var hasUsage = payload is JsonObject fields && fields.TryGetPropertyValue("usage", out usage);
            var anthropic = (node as JsonObject)?["type"]?.ToString() is "message_start" or "message_delta";
            if (report.Count == 0 && model is null && !hasUsage) return;
            lock (_gate)
            {
                var attempt = _attempts[number - 1];
                var attemptChanged = MergeReasoning(attempt, report);
                if (model is not null && attempt["reportedModel"]?.ToString() != model)
                { attempt["reportedModel"] = model; attemptChanged = true; }
                var tokensChanged = hasUsage && _tokenUsage[number - 1].Observe(usage, anthropic);
                if (tokensChanged)
                { attempt["tokenUsage"] = _tokenUsage[number - 1].ToJson(); attemptChanged = true; }
                var summaryChanged = false;
                if (number == _attempts.Count)
                {
                    summaryChanged = MergeReasoning(_info, report);
                    if (model is not null && _info["reportedModel"]?.ToString() != model)
                    { _info["reportedModel"] = model; summaryChanged = true; }
                    if (tokensChanged)
                    { _info["tokenUsage"] = _tokenUsage[number - 1].ToJson(); summaryChanged = true; }
                }
                if (attemptChanged) SaveAttempt(number);
                if (summaryChanged) Save();
            }
        }
        catch (JsonException) { }
    }
    private sealed class CapturedContent : HttpContent
    {
        private readonly HttpContent _original;
        private readonly RequestLogCapture _capture;
        private readonly int _number;
        public CapturedContent(HttpContent original, RequestLogCapture capture, int number)
        {
            _original = original; _capture = capture; _number = number;
            foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        private Stream? _stream;
        protected override async Task<Stream> CreateContentReadStreamAsync()
            => _stream ??= new CapturedStream(await _original.ReadAsStreamAsync(), _capture, _number,
                string.Equals(_original.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase));
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => _stream ??= new CapturedStream(await _original.ReadAsStreamAsync(cancellationToken), _capture, _number,
                string.Equals(_original.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase));
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await (await CreateContentReadStreamAsync()).CopyToAsync(stream);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => await (await CreateContentReadStreamAsync(cancellationToken)).CopyToAsync(stream, cancellationToken);
        protected override bool TryComputeLength(out long length)
        { length = _original.Headers.ContentLength ?? 0; return _original.Headers.ContentLength.HasValue; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _stream?.Dispose(); _original.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private sealed class CapturedStream(Stream source, RequestLogCapture capture, int number, bool sse) : Stream
    {
        private const int MetadataLimit = 1024 * 1024;
        private readonly SseEventReader? _events = sse ? new(MetadataLimit, strictUtf8: false, skipOversizeEvents: true) : null;
        private readonly Func<string, bool> _report = text => { capture.Report(number, text); return false; };
        private readonly StringBuilder? _text = sse ? null : new();
        private readonly Decoder? _decoder = sse ? null : Encoding.UTF8.GetDecoder();
        private readonly char[]? _chars = sse ? null : new char[4096];
        private bool _oversize;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var n = source.Read(buffer, offset, count); if (count > 0) Observe(buffer.AsSpan(offset, n)); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var n = await source.ReadAsync(buffer, cancellationToken); if (!buffer.IsEmpty) Observe(buffer.Span[..n]); return n; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private void Observe(ReadOnlySpan<byte> bytes)
        {
            if (!bytes.IsEmpty)
            {
                lock (capture._gate)
                {
                    if (capture._info["firstByteMs"] is null)
                    { capture._info["firstByteMs"] = capture._watch.ElapsedMilliseconds; capture.Save(); }
                }
            }
            if (_oversize) return;
            if (_events is not null)
            {
                try
                {
                    if (bytes.IsEmpty) _events.Complete();
                    else _events.Observe(bytes, _report);
                }
                catch (Exception error) when (error is IOException or InvalidDataException)
                { _oversize = true; _events.Dispose(); }
                return;
            }
            var eof = bytes.IsEmpty;
            bool complete;
            do
            {
                _decoder!.Convert(bytes, _chars.AsSpan(), eof, out var used, out var count, out complete);
                if (_text!.Length + count > MetadataLimit)
                { _text.Clear(); _oversize = true; return; }
                _text.Append(_chars.AsSpan(0, count));
                bytes = bytes[used..];
            } while (!complete);
            if (eof) capture.Report(number, _text!.ToString());
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { _events?.Dispose(); source.Dispose(); } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public AdapterResponse Attach(AdapterResponse response)
    {
        if (response.RawStream is not { } stream)
        {
            if (response.Usage is { } usage)
            {
                lock (_gate)
                {
                    if (_tokenUsage.Count > 0 && _tokenUsage[^1].FillMissing(usage))
                    {
                        _attempts[^1]["tokenUsage"] = _tokenUsage[^1].ToJson();
                        _info["tokenUsage"] = _tokenUsage[^1].ToJson();
                        SaveAttempt(_attempts.Count); Save();
                    }
                }
            }
            Complete(response.StatusCode is >= 200 and < 300 ? "completed" : "failed", response.StatusCode);
            return response;
        }
        return new AdapterResponse
        {
            StatusCode = response.StatusCode, IsStreaming = response.IsStreaming, Completion = response.Completion,
            Stream = response.Stream, Error = response.Error, ErrorType = response.ErrorType, Usage = response.Usage,
            IsRawPassthrough = response.IsRawPassthrough, RawContent = response.RawContent, ContentType = response.ContentType,
            RawStream = ObserveOutput(stream, response.StatusCode),
            Lifetime = new CapturedLifetime(response.Lifetime, this, response.StatusCode)
        };
    }
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ObserveOutput(IAsyncEnumerable<ReadOnlyMemory<byte>> source,
        int status, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool next;
                try { next = await enumerator.MoveNextAsync(); }
                catch (Exception error)
                { CompleteInterrupted(error is OperationCanceledException ? "cancelled" : "failed", status, error.Message); throw; }
                if (!next) { Complete(status is >= 200 and < 300 ? "completed" : "failed", status); yield break; }
                yield return enumerator.Current;
            }
        }
        finally { CompleteInterrupted("interrupted", status); }
    }
    private sealed class CapturedLifetime(IAsyncDisposable? original, RequestLogCapture capture, int status) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if (original is not null) await original.DisposeAsync(); }
            finally { capture.CompleteInterrupted("interrupted", status); }
        }
    }
}
