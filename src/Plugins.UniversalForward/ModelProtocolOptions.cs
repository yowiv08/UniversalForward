namespace Plugins.UniversalForward;

/// <summary>模型支持协议、首选协议和原生模式。</summary>
public sealed class ModelProtocolOptions
{
    public string[] Protocols { get; set; } = ["chat"];
    public string PreferredProtocol { get; set; } = "chat";
    public bool NativeOnly { get; set; }

    internal static string Endpoint(string protocol) => protocol switch
    {
        "chat" => "/v1/chat/completions",
        "responses" => "/v1/responses",
        "messages" => "/v1/messages",
        "completions" => "/v1/completions",
        _ => throw new FormatException("协议必须是 chat、responses、messages 或 completions。")
    };

    internal void Validate()
    {
        if (Protocols is null || Protocols.Length is < 1 or > 4)
            throw new FormatException("每个模型必须选择1–4种协议。");
        foreach (var protocol in Protocols) _ = Endpoint(protocol);
        if (!Protocols.Contains(PreferredProtocol, StringComparer.Ordinal))
            throw new FormatException("首选协议必须包含在支持协议中。");
    }

    internal string Select(string incoming)
    {
        Validate();
        if (Protocols.Any(p => Endpoint(p) == incoming)) return incoming;
        if (NativeOnly) throw new FormatException("该模型启用强制原生模式，不允许切换客户端请求接口。");
        var target = Endpoint(PreferredProtocol);
        if (incoming == "/v1/completions" || target == "/v1/completions")
            throw new FormatException("Completions 仅支持同协议请求。");
        return target;
    }
}
