namespace AiGateway.Configuration;

internal record MappingRule : ModelRoute
{
    public string Prefix { get; init; } = "";

    internal MappingRule NormalizeProxyServers()
    {
        var servers = ProxyServers;
        if (servers is null && ProxyServer is not null)
            servers = ProxyServer.Length == 0 ? [] : [ProxyServer];

        if (servers is not null)
            servers = servers.Distinct(StringComparer.Ordinal).ToList();

        return this with { ProxyServers = servers, ProxyServer = null };
    }
}
