using AiGateway.Configuration;

namespace AiGateway.Proxy;

internal record MapResult(string TargetModel, string? ProxyServer);

internal sealed class ModelMapper
{
    private readonly RuntimeMappingStore _store;

    public ModelMapper(RuntimeMappingStore store) => _store = store;

    public MapResult Map(string model)
    {
        var current = model;
        string? proxyServer = null;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (visited.Add(current))
        {
            var rule = _store.Rules.FirstOrDefault(r =>
                current.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
                break;

            proxyServer ??= string.IsNullOrEmpty(rule.ProxyServer) ? null : rule.ProxyServer;
            if (string.IsNullOrEmpty(rule.Target))
                break;

            current = rule.Target;
        }

        return new MapResult(current, proxyServer);
    }
}