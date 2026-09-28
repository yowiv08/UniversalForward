using System.Globalization;

namespace Plugins.UniversalForward;

/// <summary>重试状态码规则。</summary>
internal sealed class ForwardRetryRules
{
    internal const string DefaultCodes =
        "100-199,300-399,401-407,409-499,500-503,505-523,525-599";
    private readonly HashSet<int> _codes;

    private ForwardRetryRules(HashSet<int> codes) => _codes = codes;

    internal bool Contains(int code) => _codes.Contains(code);

    internal static ForwardRetryRules Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var codes = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(value))
            return new ForwardRetryRules(codes);
        foreach (var item in value.Split(','))
        {
            var range = item.Trim().Split('-');
            if (range.Length is < 1 or > 2)
                throw new FormatException("状态码必须是单个整数或包含性区间。");
            var first = ParseCode(range[0]);
            var last = range.Length == 2 ? ParseCode(range[1]) : first;
            if (last < first)
                throw new FormatException("状态码区间起点不能大于终点。");
            for (var code = first; code <= last; code++)
                codes.Add(code);
        }
        return new ForwardRetryRules(codes);
    }

    private static int ParseCode(string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || code is < 100 or > 599)
            throw new FormatException("状态码必须在 100–599 之间。");
        return code;
    }
}
