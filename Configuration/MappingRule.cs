namespace AiGateway.Configuration;

internal record MappingRule : ModelRoute
{
    public string Prefix { get; init; } = "";
}
