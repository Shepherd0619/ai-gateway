using System.Diagnostics;
using AiGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AiGateway.Health;

internal sealed record BackendHealthResult(
    string Backend,
    string Upstream,
    string Status,
    long LatencyMs,
    string? Error);

internal static class HealthEndpoints
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            IHttpClientFactory hcf,
            IOptions<BackendOptions> backendOptions,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var results = await CheckBackendsAsync(hcf, backendOptions.Value, logger, ct);
            var healthy = results.All(result => result.Status == "healthy");

            return Results.Json(new
            {
                status = healthy ? "healthy" : "unhealthy",
                backends = results.ToDictionary(
                    result => result.Backend,
                    result => new
                    {
                        status = result.Status,
                        upstream = result.Upstream,
                        latency_ms = result.LatencyMs,
                        error = result.Error
                    })
            }, statusCode: healthy ? 200 : 503);
        });
    }

    internal static async Task<IReadOnlyList<BackendHealthResult>> CheckBackendsAsync(
        IHttpClientFactory hcf,
        BackendOptions backends,
        ILogger logger,
        CancellationToken ct)
    {
        var checks = backends.Select((entry, _) => CheckBackendAsync(
            hcf,
            entry.Key,
            entry.Value,
            logger,
            ct));

        return await Task.WhenAll(checks);
    }

    private static async Task<BackendHealthResult> CheckBackendAsync(
        IHttpClientFactory hcf,
        string backendName,
        BackendConfig backend,
        ILogger logger,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var client = hcf.CreateClient($"backend-{backendName}");
            using var request = new HttpRequestMessage(HttpMethod.Head, backend.BaseUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            logger.LogDebug("Health check OK: {Backend} {Url} {LatencyMs}ms", backendName, backend.BaseUrl, sw.ElapsedMilliseconds);
            return new BackendHealthResult(backendName, backend.BaseUrl, "healthy", sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning("Health check timeout: {Backend} {Url} {LatencyMs}ms", backendName, backend.BaseUrl, sw.ElapsedMilliseconds);
            return new BackendHealthResult(backendName, backend.BaseUrl, "unhealthy", sw.ElapsedMilliseconds, "Upstream request timed out after 5s");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            logger.LogWarning("Health check FAIL: {Backend} {Url} {LatencyMs}ms {Error}", backendName, backend.BaseUrl, sw.ElapsedMilliseconds, ex.Message);
            return new BackendHealthResult(backendName, backend.BaseUrl, "unhealthy", sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
