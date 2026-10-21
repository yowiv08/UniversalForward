using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UniversalForward.HostIntegration;

/// <summary>将普通 HTTP 和 CONNECT 隧道转到指定回环上游，保留宿主真实代理传输路径。</summary>
internal sealed class LoopbackProxy : IAsyncDisposable
{
    private static readonly byte[] TunnelEstablished = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(15));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _completion;

    public int Port { get; }

    public LoopbackProxy(int upstreamPort)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _completion = RunAsync(upstreamPort);
    }

    private async Task RunAsync(int upstreamPort)
    {
        var cancellation = _lifetime.Token;
        using var downstream = await _listener.AcceptTcpClientAsync(cancellation);
        var input = downstream.GetStream();
        var headers = await ReadHeadersAsync(input, cancellation);
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, cancellation);
        var output = upstream.GetStream();
        if (Encoding.ASCII.GetString(headers).StartsWith("CONNECT ", StringComparison.Ordinal))
            await input.WriteAsync(TunnelEstablished, cancellation);
        else
            await output.WriteAsync(headers, cancellation);

        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var upload = input.CopyToAsync(output, relay.Token);
        var download = output.CopyToAsync(input, relay.Token);
        await Task.WhenAny(upload, download);
        await relay.CancelAsync();
        try { await Task.WhenAll(upload, download); }
        catch (OperationCanceledException) when (relay.IsCancellationRequested) { }
        catch (IOException) { /* The plugin closes upgraded streams once a terminal event is observed. */ }
    }

    private static async Task<byte[]> ReadHeadersAsync(Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[65536];
        for (var count = 0; count < buffer.Length;)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), cancellation);
            if (read == 0) throw new EndOfStreamException("Proxy connection ended before request headers");
            count += read;
            if (count >= 4 && buffer.AsSpan(count - 4, 4).SequenceEqual("\r\n\r\n"u8))
                return buffer.AsSpan(0, count).ToArray();
        }
        throw new InvalidDataException("Proxy request headers exceed the local fixture limit");
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        try { await _completion; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        _lifetime.Dispose();
    }
}
