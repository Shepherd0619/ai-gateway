namespace AiGateway.Compliance;

internal record ComplianceEntry
{
    public string Timestamp { get; init; } = "";
    public string Path { get; init; } = "";
    public string Method { get; init; } = "";
    public string ClientIp { get; init; } = "";
    public string? OriginalModel { get; init; }
    public string? UpstreamModel { get; init; }
    public int StatusCode { get; init; }
    public long DurationMs { get; init; }
    public string RequestBody { get; init; } = "";
    public string ResponseBody { get; init; } = "";
}
