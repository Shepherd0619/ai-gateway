using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiGateway.Authentication;

internal sealed record ClientCredential(bool IsGateway, string? ByokKey = null, Guid? KeyId = null);

internal sealed class ClientAuthentication(IOptions<GatewayKeyOptions> options, ILogger<ClientAuthentication> logger, GatewayKeyStore? keys = null)
{
    private static readonly object ResultKey = new();
    public bool GatewayKeysEnabled => options.Value.Enabled;

    public async Task<ClientCredential?> AuthenticateAsync(HttpContext ctx, bool allowAnonymous = false)
    {
        if (ctx.Items.TryGetValue(ResultKey, out var cached)) return (ClientCredential)cached!;
        var headers = ctx.Request.Headers;
        var xkeys = headers["x-api-key"];
        var authorization = headers["Authorization"];
        if (xkeys.Count > 1 || authorization.Count > 1)
            return await Reject(ctx, 400, "invalid_request_error", "Multiple authentication header values are not supported.");
        string? bearer = null;
        if (authorization.Count != 0)
        {
            var value = authorization[0] ?? "";
            if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return await Reject(ctx, 400, "invalid_request_error", "Authorization must use the Bearer scheme.");
            bearer = value[7..].Trim();
        }
        var xkey = xkeys.FirstOrDefault();
        if (xkeys.Count != 0 && authorization.Count != 0 && !string.Equals(xkey, bearer, StringComparison.Ordinal))
            return await Reject(ctx, 400, "invalid_request_error", "Conflicting authentication headers.");
        var token = xkey ?? bearer;
        if (string.IsNullOrEmpty(token))
        {
            if (allowAnonymous && !GatewayKeysEnabled && xkeys.Count == 0 && authorization.Count == 0)
                return Cache(ctx, new(false));
            return await Reject(ctx, 401, "authentication_error", "Missing or invalid API key.");
        }
        if (token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || token.Contains(','))
            return await Reject(ctx, 401, "authentication_error", "Missing or invalid API key.");
        if (!GatewayKeyStore.IsGatewayKey(token)) return Cache(ctx, new(false, token));
        if (!GatewayKeysEnabled) return await InvalidKey(ctx);
        if (keys is null) return await Unavailable(ctx);

        ValidatedGatewayKey? valid;
        try { valid = await keys.AuthenticateAsync(token, ctx.RequestAborted); }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            logger.LogWarning("Gateway key authentication database unavailable.");
            return await Unavailable(ctx);
        }
        if (valid is null) return await InvalidKey(ctx);

        try
        {
            var ip = ctx.Connection.RemoteIpAddress;
            await keys.RecordUseAsync(valid.Id, valid.AuthenticatedAt, ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4().ToString() : ip?.ToString(), ctx.RequestAborted);
        }
        catch (Exception ex) when (IsDatabaseError(ex))
        {
            logger.LogWarning("Gateway key last-use tracking failed for {KeyId}.", valid.Id);
        }
        return Cache(ctx, new(true, KeyId: valid.Id));
    }

    internal static bool IsDatabaseError(Exception ex) => ex is DbException or DbUpdateException or InvalidOperationException;

    internal static async Task<ClientCredential?> Reject(HttpContext ctx, int status, string type, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { type, message } }), ctx.RequestAborted);
        return null;
    }

    private static Task<ClientCredential?> InvalidKey(HttpContext ctx) => Reject(ctx, 401, "authentication_error", "Missing or invalid API key.");
    private static Task<ClientCredential?> Unavailable(HttpContext ctx) => Reject(ctx, 503, "authentication_unavailable", "Gateway key authentication is temporarily unavailable.");
    private static ClientCredential Cache(HttpContext ctx, ClientCredential value) { ctx.Items[ResultKey] = value; return value; }
}
