namespace Plugins.UniversalForward;

/// <summary>响应资源与超时管理。</summary>
internal sealed class ForwardResponseLifetime(
    HttpResponseMessage response, CancellationTokenSource total, HttpClient? client = null) : IAsyncDisposable
{
    private int _disposed;
    internal HttpResponseMessage Response { get; } = response;
    internal CancellationToken Token => total.Token;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { total.Cancel(); }
            finally
            {
                try { Response.Dispose(); }
                finally { client?.Dispose(); total.Dispose(); }
            }
        }
        return ValueTask.CompletedTask;
    }
}
