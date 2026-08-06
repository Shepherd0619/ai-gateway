namespace AiGateway.Configuration;

internal record CorsOptions
{
    public string[] AllowedOrigins { get; init; } = [];
}