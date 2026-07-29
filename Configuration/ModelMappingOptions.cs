namespace AiGateway.Configuration;

internal record ModelMappingOptions
{
    public List<MappingRule> Rules { get; init; } = [];
}
