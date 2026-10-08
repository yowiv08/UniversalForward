using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UniversalForward.Tests;

internal sealed class WebSocketLoopback : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));
    private readonly X509Certificate2? _certificate;
    private int _requests;
    public Uri Address { get; }
    public Task Completion { get; }
    public int Requests => Volatile.Read(ref _requests);

    public WebSocketLoopback(Func<Peer, int, CancellationToken, Task> handle, int connections = 1, bool tls = false)
    {
        if (tls)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // Import a transient key container for Windows Schannel's server credentials.
            _certificate = X509CertificateLoader.LoadPkcs12(issued.Export(X509ContentType.Pfx), password: null);
        }
        _listener.Start();
        Address = new Uri($"{(tls ? "https" : "http")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/responses");
        Completion = RunAsync(handle, connections);
    }

    public HttpClient CreateClient(DelegatingHandler? handler = null, Uri? proxy = null)
    {
        var sockets = new SocketsHttpHandler { UseProxy = proxy is not null, AllowAutoRedirect = false,
            Proxy = proxy is null ? null : new WebProxy(proxy) };
        if (_certificate is { } certificate)
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) => remote?.GetCertHashString() == certificate.GetCertHashString();
        if (handler is DelegatingHandler delegating) delegating.InnerHandler = sockets;
        return new HttpClient((HttpMessageHandler?)handler ?? sockets) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async Task RunAsync(Func<Peer, int, CancellationToken, Task> handle, int connections)
    {
        var token = _timeout.Token;
        for (var index = 0; index < connections; index++)
        {
            using var connection = await _listener.AcceptTcpClientAsync(token);
            using var stream = connection.GetStream();
            using var secure = _certificate is null ? null : new SslStream(stream, leaveInnerStreamOpen: true);
            if (secure is not null)
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, token);
            var transport = (Stream?)secure ?? stream;
            var head = await ReadHeadersAsync(transport, token);
            Interlocked.Increment(ref _requests);
            await handle(new Peer(transport, head), index, token);
        }
    }

    internal static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var one = new byte[1];
        while (bytes.Length < 32768)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Client closed before HTTP headers");
            bytes.WriteByte(one[0]);
            if (bytes.Length >= 4 && bytes.GetBuffer().AsSpan((int)bytes.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("HTTP headers exceed fixture limit");
    }

    public async ValueTask DisposeAsync()
    {
        await _timeout.CancelAsync();
        _listener.Stop();
        try { await Completion; }
        catch (OperationCanceledException) when (_timeout.IsCancellationRequested) { }
        finally { _timeout.Dispose(); _certificate?.Dispose(); }
    }

    internal sealed class Peer
    {
        private readonly Stream _stream;
        public string RequestLine { get; }
        public Dictionary<string, string> Headers { get; }
        public Peer(Stream stream, string head)
        {
            _stream = stream;
            var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            RequestLine = lines[0];
            Headers = lines.Skip(1).Select(line => line.Split(':', 2))
                .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        }

        public async Task<WebSocket> AcceptAsync(CancellationToken token, bool invalidAccept = false)
        {
            Assert.AreEqual("websocket", Headers["Upgrade"], ignoreCase: true);
            StringAssert.Contains(Headers["Connection"].ToLowerInvariant(), "upgrade");
            Assert.AreEqual("13", Headers["Sec-WebSocket-Version"]);
            // RFC 6455 requires SHA-1 for this public handshake challenge.
#pragma warning disable CA5350
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                Headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            if (invalidAccept) accept = "invalid";
            await _stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
                $"Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\nX-Request-Id: ws-fixture\r\n\r\n"), token);
            return WebSocket.CreateFromStream(_stream, isServer: true, subProtocol: null, Timeout.InfiniteTimeSpan);
        }

        public async Task RejectAsync(int status, string body, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            await _stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\n" +
                $"Content-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nX-Request-Id: rejected-fixture\r\n\r\n"), token);
            await _stream.WriteAsync(bytes, token);
        }

        public async Task ReadBodyAsync(CancellationToken token)
        {
            if (!Headers.TryGetValue("Content-Length", out var length)) return;
            var bytes = new byte[int.Parse(length, System.Globalization.CultureInfo.InvariantCulture)];
            await _stream.ReadExactlyAsync(bytes, token);
        }
    }

    internal static async Task<string> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(), token);
            Assert.AreEqual(WebSocketMessageType.Text, read.MessageType);
            bytes.Write(buffer, 0, read.Count);
            if (read.EndOfMessage) return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }

    internal static async Task SendAsync(WebSocket socket, string text, CancellationToken token, int fragmentSize = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var offset = 0; offset < bytes.Length; offset += fragmentSize)
        {
            var count = Math.Min(fragmentSize, bytes.Length - offset);
            await socket.SendAsync(bytes.AsMemory(offset, count), WebSocketMessageType.Text, offset + count == bytes.Length, token);
        }
    }
}
