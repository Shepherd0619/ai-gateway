namespace AiGateway.Configuration;

internal record MappingRule
{
    public string Prefix { get; init; } = "";
    public string Target { get; init; } = "";
    public string? Backend { get; init; }
    public string? ProxyServer { get; init; }
}
