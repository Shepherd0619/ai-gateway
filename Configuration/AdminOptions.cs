namespace AiGateway.Configuration;

internal record AdminOptions
{
    public string? ApiKey { get; init; }
    public string RuntimeConfigPath { get; init; } = "mappings-runtime.json";
}