using AiGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AiGateway.Proxy;

internal record MapResult(string TargetModel, string? ProxyServer);

internal sealed class ModelMapper
{
    private readonly ModelMappingOptions _mapping;

    public ModelMapper(IOptions<ModelMappingOptions> options) => _mapping = options.Value;

    public MapResult Map(string model)
    {
        foreach (var rule in _mapping.Rules)
        {
            if (model.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                return new MapResult(
                    string.IsNullOrEmpty(rule.Target) ? model : rule.Target,
                    string.IsNullOrEmpty(rule.ProxyServer) ? null : rule.ProxyServer);
        }
        return new MapResult(model, null);
    }
}
