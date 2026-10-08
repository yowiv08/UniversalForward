using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

internal static class UpstreamWebSocket
{
    internal static void ValidateTransport(string value)
    {
        if (value is not ("http" or "websocket"))
            throw new FormatException("responsesTransport 必须为 http 或 websocket");
    }

    internal static Uri Address(Uri uri)
        => new UriBuilder(uri)
        {
            Scheme = uri.Scheme switch
            {
                "https" or "wss" => "wss",
                "http" or "ws" => "ws",
                _ => throw new FormatException("WebSocket 上游地址必须使用 HTTP(S) 或 WS(S)")
            }
        }.Uri;

    internal static byte[] CreatePayload(byte[] payload)
    {
        var body = JsonNode.Parse(payload)!.AsObject();
        if (body["background"]?.GetValue<bool>() == true)
            throw new FormatException("WebSocket 不支持 background 请求，请使用 HTTP 上游连接");
        body.Remove("stream");
        body.Remove("background");
        body["type"] = "response.create";
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    internal static void PrepareHandshake(HttpRequestMessage request)
    {
        request.Method = HttpMethod.Get;
        request.Content?.Dispose();
        request.Content = null;
        request.Headers.Remove("Connection");
        request.Headers.Remove("Upgrade");
        foreach (var header in request.Headers.NonValidated.ToArray())
            if (header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                request.Headers.Remove(header.Key);
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        byte[] payload, RequestLogCapture? capture, int attempt, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        using var handshake = new HandshakeHandler(client, request, capture, attempt);
        using var invoker = new HttpMessageInvoker(handshake, disposeHandler: false);
        try
        {
            socket.Options.HttpVersion = HttpVersion.Version11;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            await socket.ConnectAsync(Address(request.RequestUri!), invoker, cancellationToken);
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
            var content = new StreamContent(new EventStream(socket, capture, attempt));
            content.Headers.ContentType = new("text/event-stream");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            foreach (var header in handshake.Headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
        catch (Exception error)
        {
            socket.Dispose();
            if (handshake.Rejected is { } rejected)
            {
                rejected.RequestMessage = request;
                return rejected;
            }
            if (error is WebSocketException)
                throw new HttpRequestException("上游 WebSocket 连接失败: " + (handshake.RejectionReason ?? error.Message), error);
            throw;
        }
    }

    private sealed class HandshakeHandler(HttpClient client, HttpRequestMessage source, RequestLogCapture? capture, int attempt) : HttpMessageHandler
    {
        internal HttpResponseMessage? Rejected { get; private set; }
        internal string? RejectionReason { get; private set; }
        internal Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Let HttpClient serialize each header's values; comma-joining parsed User-Agent products changes its value.
            foreach (var header in source.Headers.NonValidated)
            {
                if (request.Headers.NonValidated.Contains(header.Key)) request.Headers.Remove(header.Key);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            capture?.WebSocketHandshake(attempt, request);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.RequestMessage ??= request;
            capture?.WebSocketHandshakeResponse(attempt, response);
            foreach (var header in response.Headers) Headers[header.Key] = header.Value.ToArray();
            if (response.StatusCode != HttpStatusCode.SwitchingProtocols)
            {
                var status = (int)response.StatusCode;
                // A normal HTTP success is not a successful WebSocket upgrade.
                if (response.IsSuccessStatusCode) response.Dispose();
                else Rejected = response;
                RejectionReason = $"WebSocket 握手被拒绝: HTTP {status}，需要 101 Switching Protocols";
                throw new HttpRequestException(RejectionReason);
            }
            return response;
        }
    }

    private sealed class EventStream(ClientWebSocket socket, RequestLogCapture? capture, int attempt) : Stream
    {
        private const int MaxMessageBytes = 32 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly byte[] _receive = new byte[16384];
        private byte[] _pending = [];
        private int _offset;
        private bool _terminal;
        private int _disposed;
        private string Part => $"attempt-{attempt}-websocket-events";
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            if (_offset == _pending.Length)
            {
                if (_terminal) return 0;
                using var message = new MemoryStream();
                try
                {
                    while (true)
                    {
                        var result = await socket.ReceiveAsync(_receive.AsMemory(), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                            throw new IOException($"上游 WebSocket 已关闭，未收到结束事件 ({socket.CloseStatus}): {socket.CloseStatusDescription}");
                        if (result.MessageType != WebSocketMessageType.Text)
                            throw new InvalidDataException("上游 WebSocket 返回非文本消息");
                        if (message.Length + result.Count > MaxMessageBytes)
                            throw new InvalidDataException("上游 WebSocket 消息超过 32 MiB");
                        message.Write(_receive, 0, result.Count);
                        if (result.EndOfMessage) break;
                    }
                }
                catch (WebSocketException error) { throw new IOException("上游 WebSocket 中断: " + error.Message, error); }
                var bytes = message.ToArray();
                capture?.Observe(Part, bytes);
                capture?.Observe(Part, "\n"u8);
                string text;
                string type;
                try
                {
                    text = Utf8.GetString(bytes);
                    using var doc = JsonDocument.Parse(text);
                    type = doc.RootElement.GetProperty("type").GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(type) || type.Contains('\r') || type.Contains('\n'))
                        throw new JsonException("事件类型无效");
                    _terminal = type is "response.completed" or "response.failed" or "response.incomplete" or "error";
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException)
                { throw new InvalidDataException("上游 WebSocket 消息不是有效的 Responses 事件", error); }
                var framed = new StringBuilder().Append("event: ").Append(type).Append('\n');
                using var lines = new StringReader(text);
                while (lines.ReadLine() is { } line) framed.Append("data: ").Append(line).Append('\n');
                framed.Append('\n');
                _pending = Utf8.GetBytes(framed.ToString()); _offset = 0;
                if (_terminal) capture?.EndPart(Part, true);
            }
            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                capture?.EndPart(Part, _terminal);
                socket.Dispose();
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
