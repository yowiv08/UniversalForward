using System.Text.RegularExpressions;

namespace Plugins.UniversalForward;

/// <summary>模型支持协议、首选协议和原生模式。</summary>
public sealed partial class ModelProtocolOptions
{
    public string[] Protocols { get; set; } = ["responses"];
    public string PreferredProtocol { get; set; } = "responses";
    public bool NativeOnly { get; set; }

    internal static string Endpoint(string protocol) => protocol switch
    {
        "responses" => "/v1/responses",
        "messages" => "/v1/messages",
        "chat" => "/v1/chat/completions",
        _ => throw new FormatException("协议必须是 responses、messages 或 chat。")
    };

    internal static string Protocol(string endpoint) => endpoint switch
    {
        "/v1/responses" => "responses",
        "/v1/messages" => "messages",
        "/v1/chat/completions" => "chat",
        _ => throw new FormatException("仅支持 Responses、Anthropic Messages 和 Chat Completions。")
    };

    // Defaults are suggestions for generated tests; forwarding keeps the incoming protocol.
    internal static string DefaultTestEndpoint(string model, string[] endpoints)
        => ClaudeModel().IsMatch(model) && endpoints.Contains("/v1/messages") ? "/v1/messages"
            : endpoints.Contains("/v1/responses") ? "/v1/responses"
            : endpoints.FirstOrDefault() ?? throw new FormatException("渠道没有允许端点");

    [GeneratedRegex(@"(?:^|[/.])claude(?:$|[-_.:\[])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeModel();

    internal void Validate()
    {
        if (Protocols is null || Protocols.Length is < 1 or > 3)
            throw new FormatException("每个模型必须选择1–3种协议。");
        foreach (var protocol in Protocols) _ = Endpoint(protocol);
        if (!Protocols.Contains(PreferredProtocol, StringComparer.Ordinal))
            throw new FormatException("首选协议必须包含在支持协议中。");
    }

    internal string Select(string incoming)
    {
        Validate();
        var protocol = Protocol(incoming);
        if (Protocols.Contains(protocol, StringComparer.Ordinal)) return incoming;
        if (NativeOnly) throw new FormatException("该模型启用强制原生模式，不允许切换客户端请求接口。");
        var target = Endpoint(PreferredProtocol);
        return target;
    }
}
