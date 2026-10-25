using Router.Contracts.Domain;

namespace Plugins.UniversalForward;

/// <summary>只决定内部重试结束后是否交回宿主，不分配渠道或改变内部重试预算。</summary>
internal sealed class ForwardHostRetry
{
    internal int Sends { get; private set; }
    internal bool HasOutput { get; private set; }
    internal bool PermanentError { get; private set; }
    internal bool DeadlineExhausted { get; set; }
    internal bool StreamTransferred { get; set; }
    internal string? Candidate { get; set; }
    internal string? StopReason { get; set; }

    internal void Sending()
    {
        Sends++;
        Candidate = null;
        StopReason = null;
        PermanentError = false;
    }

    internal void Observe(ForwardResponseAnalysis analysis)
    {
        HasOutput |= analysis.HasOutput;
        PermanentError |= analysis.PermanentError;
    }

    internal PluginAttemptDecision Decide(PluginInvocationResult result, bool cancelled, bool connectionTest)
    {
        var prior = result.Attempt.Decision!;
        var reason = connectionTest ? "connection_test"
            : cancelled ? "request_cancelled"
            : result.Response.IsSuccess ? "success"
            : DeadlineExhausted ? "total_timeout"
            : StreamTransferred || result.Response.IsStreaming ? "stream_transferred"
            : HasOutput ? "output_observed"
            : PermanentError ? "permanent_error"
            : result.Attempt.StatusCode is 400 or 413 or 415 or 422
                || result.Response.StatusCode is 400 or 413 or 415 or 422 ? "input_error"
            : StopReason;
        var retry = reason is null && Candidate is not null;
        return prior with
        {
            Retry = retry ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
            ReasonCode = reason ?? Candidate ?? "retry_not_configured",
            FailureKind = retry && prior.FailureKind == PluginFailureKind.None ? PluginFailureKind.Upstream : prior.FailureKind
        };
    }
}
