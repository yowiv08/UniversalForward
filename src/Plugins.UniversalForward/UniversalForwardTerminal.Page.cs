using Router.Contracts.Plugins;

namespace Plugins.UniversalForward;

public sealed partial class UniversalForwardTerminal
{
    private static PluginMainPage CreateMainPage() => new(
        "UniversalForward 渠道管理",
        ReadPageAsset("index.html")
            .Replace("/*__STYLE__*/", ReadPageAsset("styles.css"), StringComparison.Ordinal)
            .Replace("/*__SCRIPT__*/", ReadPageAsset("app.js"), StringComparison.Ordinal));

    private static string ReadPageAsset(string name)
    {
        using var stream = typeof(UniversalForwardTerminal).Assembly
            .GetManifestResourceStream($"Plugins.UniversalForward.Web.{name}")
            ?? throw new InvalidOperationException($"页面资源不存在：{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
