using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Authentication;

internal sealed record GatewayKeyView(Guid Id, string Name, DateTime CreatedAt, DateTime? ExpiresAt,
    DateTime? RevokedAt, DateTime? LastUsedAt, string? LastUsedIp);
internal sealed record ValidatedGatewayKey(Guid Id, DateTime AuthenticatedAt);

internal sealed class GatewayKeyStore(IDbContextFactory<GatewayKeyDbContext> factory, TimeProvider clock, ILogger<GatewayKeyStore> logger)
{
    internal const string Prefix = "sk-aigw-";
    public static bool IsGatewayKey(string token) => token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public async Task<(GatewayKeyView Key, string ApiKey)> IssueAsync(string name, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
            throw new ArgumentException("Name must contain 1–200 characters without control characters.");
        if (expiresAt?.UtcDateTime <= now) throw new ArgumentException("expiresAt must be in the future.");
        var id = Guid.NewGuid();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Prefix}{id:N}.{secret}";
        var row = new GatewayApiKey { Id = id, Name = name.Trim(), CreatedAt = now, ExpiresAt = expiresAt?.UtcDateTime,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)) };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Keys.Add(row);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Gateway key issued: {KeyId}", id);
        return (View(row), token);
    }

    public async Task<IReadOnlyList<GatewayKeyView>> ListAsync(int limit, int offset, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || offset < 0) throw new ArgumentException("limit must be 1–100 and offset must be nonnegative.");
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Keys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ThenByDescending(k => k.Id)
            .Skip(offset).Take(limit).Select(k => new GatewayKeyView(k.Id, k.Name, k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt, k.LastUsedIp)).ToArrayAsync(ct);
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var updated = await db.Keys.Where(k => k.Id == id && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), ct);
        if (updated > 0) logger.LogInformation("Gateway key revoked: {KeyId}", id);
        return updated > 0 || await db.Keys.AsNoTracking().AnyAsync(k => k.Id == id, ct);
    }

    public async Task<ValidatedGatewayKey?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        if (token.Length != 84 || !token.StartsWith(Prefix, StringComparison.Ordinal) || token[40] != '.' ||
            !Guid.TryParseExact(token.AsSpan(8, 32), "N", out var id) ||
            token.AsSpan(41).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"))
            return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Keys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct);
        if (row is null || !CryptographicOperations.FixedTimeEquals(row.TokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(token)))) return null;
        var now = clock.GetUtcNow().UtcDateTime;
        return row.RevokedAt is not null || row.ExpiresAt <= now ? null : new(row.Id, now);
    }

    public async Task RecordUseAsync(Guid id, DateTime at, string? ip, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Keys.Where(k => k.Id == id && (k.LastUsedAt == null || k.LastUsedAt < at))
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, at).SetProperty(k => k.LastUsedIp, ip), ct);
    }

    private static GatewayKeyView View(GatewayApiKey k) => new(k.Id, k.Name, k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt, k.LastUsedIp);
}
