using System.Diagnostics;
using AiGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AiGateway.Admin;

internal static class AdminEndpoints
{
    internal static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var adminOptions = app.ServiceProvider.GetRequiredService<IOptions<AdminOptions>>().Value;

        // When no API key is configured, admin API is disabled.
        // Return 404 for all /admin/* to avoid leaking the endpoint's existence.
        if (string.IsNullOrEmpty(adminOptions.ApiKey))
        {
            app.Map("/admin/{**catchAll}", () => Results.NotFound());
            return;
        }

        var apiKey = adminOptions.ApiKey;

        var group = app.MapGroup("/admin")
            .AddEndpointFilter(async (ctx, next) =>
            {
                var key = ctx.HttpContext.Request.Headers["x-admin-key"].FirstOrDefault();
                if (key != apiKey)
                {
                    return Results.Json(
                        new { error = "Missing or invalid x-admin-key header" },
                        statusCode: 401);
                }
                return await next(ctx);
            });

        // GET /admin/mappings — list all effective rules (merged view)
        group.MapGet("/mappings", (RuntimeMappingStore store) =>
            Results.Json(store.Rules));

        // GET /admin/mappings/{prefix} — get a single rule
        group.MapGet("/mappings/{prefix}", (RuntimeMappingStore store, string prefix) =>
        {
            prefix = Uri.UnescapeDataString(prefix);
            var rule = store.Rules.FirstOrDefault(r =>
                r.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            return rule is not null
                ? Results.Json(rule)
                : Results.NotFound();
        });

        // PUT /admin/mappings — replace all runtime rules
        group.MapPut("/mappings", (RuntimeMappingStore store, List<MappingRule> rules) =>
        {
            if (rules is null)
                return Results.Json(new { error = "Request body is required" }, statusCode: 400);

            try
            {
                store.Save(rules);
                return Results.Json(store.Rules);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        // PATCH /admin/mappings/{prefix} — upsert a single rule
        group.MapPatch("/mappings/{prefix}", (RuntimeMappingStore store, string prefix, MappingRule? body) =>
        {
            prefix = Uri.UnescapeDataString(prefix);
            if (body is null)
                return Results.Json(new { error = "Request body is required" }, statusCode: 400);

            // Prefix from URL path takes precedence over body
            var rule = body with { Prefix = prefix };

            try
            {
                store.Upsert(rule);
                var updated = store.Rules.First(r =>
                    r.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));
                return Results.Json(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        // DELETE /admin/mappings/{prefix} — remove a runtime override
        group.MapDelete("/mappings/{prefix}", (RuntimeMappingStore store, string prefix) =>
        {
            prefix = Uri.UnescapeDataString(prefix);
            store.Delete(prefix);
            return Results.NoContent();
        });

        // GET /admin/proxy-health — probe every configured ProxyServer for reachability
        group.MapGet("/proxy-health", async (
            IHttpClientFactory hcf,
            IOptions<ProxyServerOptions> proxyOptions,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var upstreamBaseUrl = config.GetValue<string>("Upstream:BaseUrl")
                ?? "https://openrouter.ai/api";

            var probes = proxyOptions.Value.Keys.Select(async name =>
            {
                var sw = Stopwatch.StartNew();
                string status;
                string? error = null;
                try
                {
                    var client = hcf.CreateClient($"openrouter-proxy-{name}");
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    using var request = new HttpRequestMessage(HttpMethod.Head, upstreamBaseUrl);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    status = "healthy";
                }
                catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                {
                    status = "unhealthy";
                    error = "Upstream request timed out after 5s";
                }
                catch (Exception)
                {
                    status = "unhealthy";
                    error = "Connection failed";
                }
                sw.Stop();

                return new { name, status, latency_ms = sw.ElapsedMilliseconds, error };
            });

            return Results.Json(await Task.WhenAll(probes));
        });
    }
}