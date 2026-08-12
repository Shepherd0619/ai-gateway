namespace AiGateway.Configuration;

internal record ProxyServerConfig
{
    public string Address { get; init; } = "";
}

internal class ProxyServerOptions : Dictionary<string, ProxyServerConfig>
{
}
