namespace AiGateway.Discovery;

internal static class ModelDiscoveryEndpoints
{
    internal static void MapModelDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", () => Results.Content("""
            {"data":[
                {"id":"claude-sonnet-4-20250514","type":"model","display_name":"Claude Sonnet 4","created_at":"2025-05-14T00:00:00Z"},
                {"id":"claude-opus-4-20250514","type":"model","display_name":"Claude Opus 4","created_at":"2025-05-14T00:00:00Z"},
                {"id":"claude-haiku-4-20250514","type":"model","display_name":"Claude Haiku 4","created_at":"2025-05-14T00:00:00Z"}
            ]}
            """, "application/json"));
    }
}
