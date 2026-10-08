using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Plugins.UniversalForward;

internal sealed class RequestLogCapture
{
    private readonly RequestLogStore _store;
    private readonly object _gate = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly JsonObject _info;
    private readonly List<JsonObject> _attempts = [];
    private readonly Dictionary<string, Part> _parts = new(StringComparer.Ordinal);
    private int _finished;
    private readonly int _limit;
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public RequestLogCapture(RequestLogStore store, string kind, string? channel, string? label, string? model,
        string? endpoint, string? network, string? trace, object? headers)
    {
        _store = store; _limit = store.Settings.BodyLimitBytes;
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _info = new JsonObject
        {
            ["id"] = Id, ["started"] = started, ["kind"] = kind, ["channel"] = channel,
            ["channelLabel"] = label, ["model"] = model, ["endpoint"] = endpoint,
            ["network"] = network, ["trace"] = trace, ["state"] = "running", ["retries"] = 0
        };
        var json = _info.ToJsonString();
        _store.Enqueue(Id, c => c.Insert(Id, json));
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
        var bytes = Encoding.UTF8.GetBytes(text);
        Observe(name, bytes); EndPart(name, true);
        lock (_gate)
        {
            if (name == "incoming") _info["receivedReasoning"] = Reasoning(text);
            if (name == "extensions") _info["extensionReasoning"] = Reasoning(text);
        }
    }
    public void Observe(string name, ReadOnlySpan<byte> bytes)
    {
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
            var text = Encoding.UTF8.GetString(body);
            var info = new JsonObject
            {
                ["number"] = number, ["method"] = request.Method.Method, ["url"] = request.RequestUri?.ToString(),
                ["transport"] = transport,
                ["startedMs"] = _watch.ElapsedMilliseconds, ["sentReasoning"] = Reasoning(text)
            };
            if (transport == "websocket") info["url"] = UpstreamWebSocket.Address(request.RequestUri!).ToString();
            _attempts.Add(info);
            _info["retries"] = number - 1;
            _info["sentReasoning"] = info["sentReasoning"]?.DeepClone();
            _info["reasoningChanged"] = !JsonNode.DeepEquals(_info["receivedReasoning"], _info["sentReasoning"]);
            if (transport != "websocket")
                AddText($"attempt-{number}-request-headers", JsonSerializer.Serialize(RequestHeaders(request), RequestLogStore.Json));
            Observe($"attempt-{number}-request", body); EndPart($"attempt-{number}-request", true);
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
    public static JsonObject Reasoning(string text)
    {
        var result = new JsonObject();
        try
        {
            var root = JsonNode.Parse(text);
            if (root is not JsonObject) return result;
            foreach (var path in new[] { "reasoning_effort", "effort", "reasoning.effort", "thinking.type",
                "thinking.effort", "thinking.budget_tokens", "output_config.effort",
                "usage.output_tokens_details.reasoning_tokens", "usage.completion_tokens_details.reasoning_tokens" })
            {
                JsonNode? value = root;
                foreach (var key in path.Split('.')) value = value is JsonObject obj ? obj[key] : null;
                if (value is JsonValue) result[path] = value.DeepClone();
            }
        }
        catch (JsonException) { }
        return result;
    }
    private void Report(int number, string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject obj && obj["response"] is JsonObject response) text = response.ToJsonString();
            var report = Reasoning(text);
            if (report.Count == 0) return;
            lock (_gate)
            {
                _attempts[number - 1]["reportedReasoning"] = report;
                _info["reportedReasoning"] = report.DeepClone(); SaveAttempt(number); Save();
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
                _original.Headers.ContentType?.MediaType == "text/event-stream");
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => _stream ??= new CapturedStream(await _original.ReadAsStreamAsync(cancellationToken), _capture, _number,
                _original.Headers.ContentType?.MediaType == "text/event-stream");
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await (await CreateContentReadStreamAsync()).CopyToAsync(stream);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => await (await CreateContentReadStreamAsync(cancellationToken)).CopyToAsync(stream, cancellationToken);
        protected override bool TryComputeLength(out long length)
        { length = _original.Headers.ContentLength ?? 0; return _original.Headers.ContentLength.HasValue; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _capture.EndPart($"attempt-{_number}-response", false); _original.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private sealed class CapturedStream(Stream source, RequestLogCapture capture, int number, bool sse) : Stream
    {
        private readonly StringBuilder _text = new();
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
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
                capture.Observe($"attempt-{number}-response", bytes);
            }
            else capture.EndPart($"attempt-{number}-response", true);
            if (_oversize) return;
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var count = _decoder.GetChars(bytes, chars, bytes.IsEmpty);
            _text.Append(chars, 0, count);
            if (sse)
            {
                var text = _text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
                int end;
                while ((end = text.IndexOf("\n\n", StringComparison.Ordinal)) >= 0)
                {
                    var frame = text[..end]; text = text[(end + 2)..];
                    capture.Report(number, string.Join("\n", frame.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart())));
                }
                _text.Clear().Append(text);
            }
            else if (bytes.IsEmpty) capture.Report(number, _text.ToString());
            if (_text.Length > 1024 * 1024) { _text.Clear(); _oversize = true; }
        }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public AdapterResponse Attach(AdapterResponse response)
    {
        if (response.RawStream is not { } stream)
        { Complete(response.StatusCode is >= 200 and < 300 ? "completed" : "failed", response.StatusCode); return response; }
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
                { Complete(error is OperationCanceledException ? "cancelled" : "failed", status, error.Message); throw; }
                if (!next) { Complete(status is >= 200 and < 300 ? "completed" : "failed", status); yield break; }
                yield return enumerator.Current;
            }
        }
        finally { Complete("interrupted", status); }
    }
    private sealed class CapturedLifetime(IAsyncDisposable? original, RequestLogCapture capture, int status) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if (original is not null) await original.DisposeAsync(); }
            finally { capture.Complete("interrupted", status); }
        }
    }
}
