using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    private static readonly (string Token, string Asset)[] ScriptModules =
    [
        ("/*__CORE__*/", "core.js"),
        ("/*__HEADERS_MAPPING__*/", "headers-mapping.js"),
        ("/*__SETTINGS__*/", "settings.js"),
        ("/*__REASONING__*/", "reasoning.js"),
        ("/*__CONNECTION_TESTS__*/", "connection-tests.js"),
        ("/*__JOURNAL__*/", "journal.js")
    ];

    private static PluginMainPage CreateMainPage()
    {
        var script = ReadPageAsset("app.js");
        foreach (var (token, asset) in ScriptModules)
        {
            if (!script.Contains(token, StringComparison.Ordinal))
                throw new InvalidOperationException($"页面缺少模块入口：{asset}");
            script = script.Replace(token, ReadPageAsset(asset), StringComparison.Ordinal);
        }

        return new(
            "UniversalForward 渠道管理",
            ReadPageAsset("index.html")
                .Replace("/*__APPEARANCE__*/", ReadPageAsset("appearance.js"), StringComparison.Ordinal)
                .Replace("/*__STYLE__*/", ReadPageAsset("styles.css"), StringComparison.Ordinal)
                .Replace("/*__SCRIPT__*/", script, StringComparison.Ordinal));
    }

    private static string ReadPageAsset(string name)
    {
        using var stream = typeof(UniversalForwardTerminal).Assembly
            .GetManifestResourceStream($"Plugins.UniversalForward.Web.{name}")
            ?? throw new InvalidOperationException($"页面资源不存在：{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
