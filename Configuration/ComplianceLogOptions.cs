namespace AiGateway.Configuration;

internal record ComplianceLogOptions
{
    public bool Enabled { get; init; }
    public string Path { get; init; } = "/var/log/ai-gateway/compliance.log";
}
