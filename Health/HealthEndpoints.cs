using System.Diagnostics;

namespace AiGateway.Health;

internal static class HealthEndpoints
{
    internal static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            IHttpClientFactory hcf,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var upstreamBaseUrl = config.GetValue<string>("Upstream:BaseUrl")
                ?? "https://openrouter.ai/api";

            var client = hcf.CreateClient("openrouter");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var sw = Stopwatch.StartNew();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, upstreamBaseUrl);
                await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();

                logger.LogDebug("Health check OK: {Url} {LatencyMs}ms", upstreamBaseUrl, sw.ElapsedMilliseconds);

                return Results.Json(new
                {
                    status = "healthy",
                    upstream = upstreamBaseUrl,
                    latency_ms = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                sw.Stop();
                logger.LogWarning("Health check timeout: {Url} {LatencyMs}ms", upstreamBaseUrl, sw.ElapsedMilliseconds);

                return Results.Json(new
                {
                    status = "unhealthy",
                    upstream = upstreamBaseUrl,
                    latency_ms = sw.ElapsedMilliseconds,
                    error = "Upstream request timed out after 5s"
                }, statusCode: 503);
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                logger.LogWarning("Health check FAIL: {Url} {LatencyMs}ms {Error}", upstreamBaseUrl, sw.ElapsedMilliseconds, ex.Message);

                return Results.Json(new
                {
                    status = "unhealthy",
                    upstream = upstreamBaseUrl,
                    latency_ms = sw.ElapsedMilliseconds,
                    error = ex.Message
                }, statusCode: 503);
            }
        });
    }
}
