namespace AiGateway.Configuration;

internal sealed record ModelDiscoveryModel
{
    public string Id { get; init; } = "";
    public string? DisplayName { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? OwnedBy { get; init; }
}
