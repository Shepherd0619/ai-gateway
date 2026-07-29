using AiGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AiGateway.Proxy;

internal sealed class ModelMapper
{
    private readonly ModelMappingOptions _mapping;

    public ModelMapper(IOptions<ModelMappingOptions> options) => _mapping = options.Value;

    public string Map(string model)
    {
        foreach (var rule in _mapping.Rules)
        {
            if (model.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                return rule.Target;
        }
        return model; // passthrough
    }
}
