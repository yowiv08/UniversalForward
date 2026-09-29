namespace Plugins.UniversalForward;
/// <summary>重试、超时和状态码映射配置。</summary>
public sealed class ForwardRequestPolicy
{
    public int MaxRetries { get; set; }
    public bool RateLimitRetryEnabled { get; set; }
    public bool EmptyResponseRetryEnabled { get; set; }
    public int ResponseMaxRetries { get; set; } = 3;
    public int ResponseRetryIntervalSeconds { get; set; } = 5;
    public string RetryStatusCodes { get; set; } = ForwardRetryRules.DefaultCodes;
    public int HeaderTimeoutSeconds { get; set; } = 60;
    public int TotalTimeoutSeconds { get; set; } = 180;
    public int StreamIdleTimeoutSeconds { get; set; } = 60;
    public Dictionary<int, int> StatusCodeMapping { get; set; } = [];

    internal void Validate()
    {
        if (ResponseMaxRetries is < 0 or > 10 || ResponseRetryIntervalSeconds is < 1 or > 3600)
            throw new FormatException("响应异常重试次数必须为 0–10，间隔必须为 1–3600 秒。");
        if (MaxRetries is < 0 or > 10)
            throw new FormatException("重试次数必须为 0–10。");
        if (HeaderTimeoutSeconds is < 1 or > 3600 || TotalTimeoutSeconds is < 1 or > 3600
            || StreamIdleTimeoutSeconds is < 1 or > 3600)
            throw new FormatException("超时必须为 1–3600 秒。");
        if (RetryStatusCodes is null || StatusCodeMapping is null)
            throw new FormatException("状态码规则及映射不能为 null。");
        _ = ForwardRetryRules.Parse(RetryStatusCodes);
        foreach (var (source, target) in StatusCodeMapping)
            if (source is < 200 or > 599 || target is < 200 or > 599
                || target is 204 or 205 or 304)
                throw new FormatException("映射必须使用 200–599，目标不得为 204、205、304。");
    }

    internal int Map(int code) => StatusCodeMapping.GetValueOrDefault(code, code);
}
