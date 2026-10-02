using System.Text.Json;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    internal RequestLogStore? RequestLogs { get; set; }
    private readonly object _journalLifecycle = new();
    private Timer? _journalRetry;
    private bool _journalStopped;
    private string? _requestLogInitializationError;
    private void InitializeRequestLogs()
    {
        lock (_journalLifecycle)
        {
            if (_journalStopped) return;
            var directory = RequestLogs?.DirectoryPath;
            if (RequestLogs is { Available: false })
            {
                RequestLogs.Dispose();
                RequestLogs = null;
            }
            if (RequestLogs is not null) return;
            try
            {
                RequestLogs = new RequestLogStore(directory ?? Environment.GetEnvironmentVariable("UNIVERSALFORWARD_LOG_DIRECTORY")
                    ?? Path.Combine(AppContext.BaseDirectory, "data", "universalforward", "request-logs"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or TypeInitializationException or DllNotFoundException or BadImageFormatException)
            { _requestLogInitializationError = error.Message; }
            _journalRetry ??= new Timer(_ => InitializeRequestLogs(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }
    private void DisposeRequestLogs()
    {
        lock (_journalLifecycle)
        {
            _journalStopped = true;
            _journalRetry?.Dispose();
            RequestLogs?.Dispose();
        }
    }
    private RequestLogCapture? BeginRequestLog(Func<RequestLogStore, RequestLogCapture?> begin)
    {
        if (RequestLogs is not { } store) return null;
        try { return begin(store); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        { store.RecordFailure(error); return null; }
    }

    [PluginEndpoint("GET", "logs/status")]
    public Task<PluginResult> RequestLogStatus(PluginHttpContext context)
        => Task.FromResult(context.Ok(RequestLogs?.Status ?? new { available = false, error = _requestLogInitializationError ?? "日志存储尚未初始化" }));

    [PluginEndpoint("GET", "logs")]
    public Task<PluginResult> ListRequestLogs(PluginHttpContext context)
        => Task.FromResult(ReadJournal(context, store => store.List(context.Query.ToDictionary(p => p.Key, p => p.Value))));

    [PluginEndpoint("GET", "logs/detail")]
    public Task<PluginResult> RequestLogDetail(PluginHttpContext context)
        => Task.FromResult(ReadJournal(context, store => store.Detail(context.Query.GetValueOrDefault("id", ""))));

    [PluginEndpoint("GET", "logs/body")]
    public Task<PluginResult> RequestLogBody(PluginHttpContext context)
        => Task.FromResult(ReadJournal(context, store => store.ReadPart(context.Query.GetValueOrDefault("id", ""),
            context.Query.GetValueOrDefault("part", ""),
            int.TryParse(context.Query.GetValueOrDefault("after"), out var after) ? Math.Max(-1, after) : -1)));

    [PluginEndpoint("POST", "logs/delete")]
    public async Task<PluginResult> DeleteRequestLogAsync(PluginHttpContext context)
    {
        if (!TryReadBody<JournalDeleteInput>(context.Body, out var input) || string.IsNullOrWhiteSpace(input.Id))
            return context.BadRequest("请指定日志 ID");
        return await WriteJournalAsync(context, async store =>
        {
            var detail = store.Detail(input.Id);
            if (detail?["state"]?.ToString() == "running") throw new FormatException("正在执行的请求不能删除");
            await store.DeleteAsync(input.Id, context.CancellationToken);
        });
    }

    [PluginEndpoint("POST", "logs/clear")]
    public Task<PluginResult> ClearRequestLogsAsync(PluginHttpContext context)
        => WriteJournalAsync(context, store => store.DeleteAsync(null, context.CancellationToken));

    [PluginEndpoint("POST", "logs/settings")]
    public Task<PluginResult> ConfigureRequestLogsAsync(PluginHttpContext context)
    {
        if (!TryReadBody<RequestLogSettings>(context.Body, out var input))
            return Task.FromResult(context.BadRequest("日志设置无效"));
        return WriteJournalAsync(context, store => store.ConfigureAsync(input, context.CancellationToken));
    }

    private PluginResult ReadJournal(PluginHttpContext context, Func<RequestLogStore, object?> read)
    {
        if (RequestLogs is not { Available: true } store) return context.Json(503, new { error = "日志存储不可用", storage = RequestLogs?.Status });
        try { var result = read(store); return result is null ? context.Json(404, new { error = "日志不存在或已清理" }) : context.Ok(result); }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or JsonException)
        { return context.Json(503, new { error = error.Message }); }
    }
    private async Task<PluginResult> WriteJournalAsync(PluginHttpContext context, Func<RequestLogStore, Task> write)
    {
        if (RequestLogs is not { Available: true } store) return context.Json(503, new { error = "日志存储不可用" });
        try { await write(store); return context.Ok(new { success = true }); }
        catch (FormatException error) { return context.BadRequest(error.Message); }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or TimeoutException)
        { return context.Json(503, new { error = error.Message }); }
    }
    private sealed record JournalDeleteInput(string Id);
}
