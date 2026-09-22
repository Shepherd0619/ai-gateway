namespace AiGateway.Configuration;

internal record ModelRoute
{
    public string Target { get; init; } = "";
    public string? Backend { get; init; }
    public string? ProxyServer { get; init; }
}
