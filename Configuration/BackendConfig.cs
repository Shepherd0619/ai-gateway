namespace AiGateway.Configuration;

internal sealed record BackendConfig
{
    public string BaseUrl { get; init; } = "";
}
