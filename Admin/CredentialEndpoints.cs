using System.Globalization;
using AiGateway.Authentication;
using Microsoft.Extensions.Options;

namespace AiGateway.Admin;

internal sealed class BackendCredentialUpdate
{
    public string? ApiKey { get; init; }
}

internal sealed record GatewayKeyCreateRequest(string Name, DateTimeOffset? ExpiresAt);

internal static class CredentialEndpoints
{
    internal static void MapCredentialEndpoints(this RouteGroupBuilder admin)
    {
        var credentials = NoStore(admin.MapGroup("/backend-credentials"));
        credentials.MapGet("", (BackendCredentialStore store) => Results.Json(new { backends = store.All }));
        credentials.MapPut("/{name}", (string name, BackendCredentialUpdate? update, BackendCredentialStore store) => SaveCredential(store, name, update));
        credentials.MapDelete("/{name}", (string name, BackendCredentialStore store) => DeleteCredential(store, name));
        credentials.MapPut("", (HttpRequest request, BackendCredentialUpdate? update, BackendCredentialStore store) =>
            SaveCredential(store, request.Query["name"].ToString(), update));
        credentials.MapDelete("", (HttpRequest request, BackendCredentialStore store) => DeleteCredential(store, request.Query["name"].ToString()));

        if (((IEndpointRouteBuilder)admin).ServiceProvider.GetService<IOptions<GatewayKeyOptions>>()?.Value.Enabled != true) return;
        var keys = NoStore(admin.MapGroup("/gateway-keys"));
        keys.MapPost("", (GatewayKeyCreateRequest? request, GatewayKeyStore store, HttpContext ctx) => DatabaseResult(async () =>
        {
            if (request is null) return BadRequest("Request body is required.");
            var issued = await store.IssueAsync(request.Name, request.ExpiresAt, ctx.RequestAborted);
            var k = issued.Key;
            return Results.Json(new { k.Id, k.Name, k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt, k.LastUsedIp, issued.ApiKey }, statusCode:201);
        }));
        keys.MapGet("", (HttpContext ctx, GatewayKeyStore store) => DatabaseResult(async () =>
        {
            var query = ctx.Request.Query;
            if (!TryPage(query, "limit", 50, out var limit) || !TryPage(query, "offset", 0, out var offset))
                return BadRequest("Invalid pagination parameters.");
            return Results.Json(new { keys = await store.ListAsync(limit, offset, ctx.RequestAborted), limit, offset });
        }));
        keys.MapPost("/{id}/revoke", (string id, GatewayKeyStore store, HttpContext ctx) => DatabaseResult(async () =>
        {
            if (!Guid.TryParse(id, out var parsed)) return BadRequest("Invalid gateway key id.");
            return await store.RevokeAsync(parsed, ctx.RequestAborted) ? Results.NoContent() : Results.NotFound();
        }));
    }

    private static RouteGroupBuilder NoStore(RouteGroupBuilder group) => group.AddEndpointFilter(async (context, next) =>
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    });

    private static IResult SaveCredential(BackendCredentialStore store, string name, BackendCredentialUpdate? update)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Backend name is required.");
        if (update?.ApiKey is null) return BadRequest("apiKey is required.");
        try { return Results.Json(store.Save(name, update.ApiKey)); }
        catch (KeyNotFoundException) { return Results.NotFound(new { error = "Backend is not configured." }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static IResult DeleteCredential(BackendCredentialStore store, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Backend name is required.");
        try { return Results.Json(store.Delete(name)); }
        catch (KeyNotFoundException) { return Results.NotFound(new { error = "Backend is not configured." }); }
    }

    private static async Task<IResult> DatabaseResult(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (Exception ex) when (ClientAuthentication.IsDatabaseError(ex))
        { return Results.Json(new { error = "Gateway key storage is temporarily unavailable." }, statusCode:503); }
    }

    private static bool TryPage(IQueryCollection query, string name, int fallback, out int value)
    {
        value = fallback;
        return !query.TryGetValue(name, out var text) || (text.Count == 1 && int.TryParse(text[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out value));
    }

    private static IResult BadRequest(string message) => Results.Json(new { error = message }, statusCode:400);
}
