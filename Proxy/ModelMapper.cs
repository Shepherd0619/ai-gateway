using AiGateway.Configuration;

namespace AiGateway.Proxy;

internal record MapResult(string TargetModel, string? ProxyServer);

internal sealed class ModelMapper
{
    private readonly RuntimeMappingStore _store;

    public ModelMapper(RuntimeMappingStore store) => _store = store;

    public MapResult Map(string model)
    {
        foreach (var rule in _store.Rules)
        {
            if (model.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                return new MapResult(
                    string.IsNullOrEmpty(rule.Target) ? model : rule.Target,
                    string.IsNullOrEmpty(rule.ProxyServer) ? null : rule.ProxyServer);
        }
        return new MapResult(model, null);
    }
}