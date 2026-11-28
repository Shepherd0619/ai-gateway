using AiGateway.Configuration;

namespace AiGateway.Proxy;

internal record MapResult(string TargetModel, string Backend, string? ProxyServer)
{
    private IReadOnlyList<string>? _proxyServers;

    public IReadOnlyList<string> ProxyServers
    {
        get => _proxyServers ?? (ProxyServer is null ? [] : [ProxyServer]);
        init => _proxyServers = value;
    }
}

internal sealed class ModelMapper
{
    private readonly RuntimeMappingStore _store;

    public ModelMapper(RuntimeMappingStore store) => _store = store;

    public MapResult Map(string model)
    {
        var current = model;
        var backend = BackendOptions.DefaultBackendName;
        var backendSelected = false;
        var proxySelected = false;
        IReadOnlyList<string> proxyServers = [];
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (visited.Add(current))
        {
            var rule = _store.Rules.FirstOrDefault(r =>
                current.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
                break;

            if (!backendSelected && !string.IsNullOrEmpty(rule.Backend))
            {
                backend = rule.Backend;
                backendSelected = true;
            }

            if (!proxySelected && rule.ProxyServers is not null)
            {
                proxyServers = rule.ProxyServers;
                proxySelected = true;
            }

            if (string.IsNullOrEmpty(rule.Target))
                break;

            current = rule.Target;
        }

        return new MapResult(current, backend, proxyServers.FirstOrDefault())
        {
            ProxyServers = proxyServers
        };
    }
}
