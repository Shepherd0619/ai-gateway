namespace AiGateway.Configuration;

internal sealed record ModelDiscoveryOptions
{
    public bool Enabled { get; init; }
    public List<ModelDiscoveryModel> Models { get; init; } = [];
}
