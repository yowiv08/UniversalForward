namespace Plugins.UniversalForward;

/// <summary>模型支持协议、首选协议和原生模式。</summary>
public sealed class ModelProtocolOptions
{
    public string[] Protocols { get; set; } = ["responses"];
    public string PreferredProtocol { get; set; } = "responses";
    public bool NativeOnly { get; set; }

    internal static string Endpoint(string protocol) => protocol switch
    {
        "responses" => "/v1/responses",
        "messages" => "/v1/messages",
        _ => throw new FormatException("协议必须是 responses 或 messages。")
    };

    internal void Validate()
    {
        if (Protocols is null || Protocols.Length is < 1 or > 2)
            throw new FormatException("每个模型必须选择1–2种协议。");
        foreach (var protocol in Protocols) _ = Endpoint(protocol);
        if (!Protocols.Contains(PreferredProtocol, StringComparer.Ordinal))
            throw new FormatException("首选协议必须包含在支持协议中。");
    }

    internal string Select(string incoming)
    {
        Validate();
        if (incoming is not ("/v1/responses" or "/v1/messages"))
            throw new FormatException("仅支持 Responses 和 Anthropic Messages。");
        if (Protocols.Any(p => Endpoint(p) == incoming)) return incoming;
        if (NativeOnly) throw new FormatException("该模型启用强制原生模式，不允许切换客户端请求接口。");
        var target = Endpoint(PreferredProtocol);
        return target;
    }
}
