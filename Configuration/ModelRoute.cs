using System.Text.Json.Serialization;

namespace AiGateway.Configuration;

internal record ModelRoute
{
    public string Target { get; init; } = "";
    public string? Backend { get; init; }
    public List<string>? ProxyServers { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProxyServer { get; init; }
}
