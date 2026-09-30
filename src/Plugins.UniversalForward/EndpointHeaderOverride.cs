using System.Text.Json.Nodes;

namespace Plugins.UniversalForward;

public sealed class EndpointHeaderOverride
{
    public bool UseCommon { get; set; } = true;
    public JsonObject Headers { get; set; } = new();
}
