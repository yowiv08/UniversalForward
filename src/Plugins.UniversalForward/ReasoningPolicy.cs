using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

/// <summary>渠道默认和模型独立覆盖的顶层思考等级规则。</summary>
public sealed class ReasoningPolicy
{
    public Dictionary<string, ReasoningRule> Defaults { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, ReasoningRule>> Models { get; set; } = new(StringComparer.Ordinal);

    internal void Validate(IReadOnlyCollection<string>? allowedModels = null)
    {
        if (Defaults is null || Models is null) throw new FormatException("思考映射 defaults 和 models 必须为对象");
        ValidateRules(Defaults);
        foreach (var (model, rules) in Models)
        {
            if (string.IsNullOrWhiteSpace(model) || allowedModels is not null && !allowedModels.Contains(model, StringComparer.Ordinal))
                throw new FormatException("思考映射必须引用当前允许模型");
            ValidateRules(rules);
        }
    }

    private static void ValidateRules(Dictionary<string, ReasoningRule>? rules)
    {
        if (rules is null) throw new FormatException("思考映射接口配置必须为对象");
        foreach (var (protocol, rule) in rules)
        {
            if (protocol is not ("responses" or "messages") || rule is null)
                throw new FormatException("思考映射接口必须是 responses 或 messages，规则不能为空");
            rule.Validate();
        }
    }

    // Resolve against the original body, before profiles add defaults or alter the payload.
    internal ReasoningMappingDecision? Resolve(JsonObject original, string model, string endpoint)
    {
        var protocol = endpoint == "/v1/messages" ? "messages" : "responses";
        var source = "channel";
        if (!Models.TryGetValue(model, out var overrides) || !overrides.TryGetValue(protocol, out var rule))
            Defaults.TryGetValue(protocol, out rule);
        else source = "model";
        if (rule is null || rule.Mode == "off") return null;

        var parent = original[protocol == "responses" ? "reasoning" : "output_config"];
        var value = (parent as JsonObject)?["effort"];
        var missing = parent is null || parent is JsonObject && value is null;
        var input = value is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
        var target = rule.Mode == "fixed" ? rule.FixedEffort
            : missing ? rule.DefaultEffort
            : input is not null ? rule.Mappings.FirstOrDefault(pair =>
                pair.From.Equals(input.Trim(), StringComparison.OrdinalIgnoreCase))?.To : null;
        if (target is null) return null;
        return new(source, model, protocol, rule.Mode,
            rule.Mode == "fixed" ? "fixed" : missing ? "default" : "mapped", value?.DeepClone(), target);
    }

    internal static void Apply(JsonObject prepared, ReasoningMappingDecision decision)
    {
        var name = decision.Protocol == "responses" ? "reasoning" : "output_config";
        if (prepared[name] is not null and not JsonObject)
            throw new FormatException($"应用思考映射时 {name} 必须为对象");
        var parent = prepared[name] as JsonObject;
        if (parent is null) prepared[name] = parent = new JsonObject();
        parent["effort"] = decision.Sent;
    }
}

public sealed class ReasoningRule
{
    public string Mode { get; set; } = "off";
    public List<ReasoningMap> Mappings { get; set; } = [];
    public string? DefaultEffort { get; set; }
    public string? FixedEffort { get; set; }

    internal void Validate()
    {
        if (Mode is not ("off" or "map" or "fixed")) throw new FormatException("思考映射模式必须是 off、map 或 fixed");
        if (Mappings is null) throw new FormatException("思考映射 mappings 必须为数组");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Mappings)
        {
            if (pair is null || string.IsNullOrWhiteSpace(pair.From) || string.IsNullOrWhiteSpace(pair.To))
                throw new FormatException("思考映射的来源和目标等级不能为空");
            pair.From = pair.From.Trim();
            pair.To = pair.To.Trim();
            if (!sources.Add(pair.From)) throw new FormatException($"思考映射来源等级重复：{pair.From}");
        }
        DefaultEffort = string.IsNullOrWhiteSpace(DefaultEffort) ? null : DefaultEffort.Trim();
        FixedEffort = string.IsNullOrWhiteSpace(FixedEffort) ? null : FixedEffort.Trim();
        if (Mode == "fixed" && FixedEffort is null) throw new FormatException("固定思考等级不能为空");
    }
}

public sealed class ReasoningMap
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

internal sealed record ReasoningMappingDecision(
    string Source, string Model, string Protocol, string Mode, string Reason, JsonNode? Received, string Sent);
