using System.Security.Cryptography;
using System.Text;
using AiGateway.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiGateway.Tests;

public sealed class GatewayKeyStoreTests
{
    [Fact]
    public async Task IssuePersistsOnlyDigestAndRevocationSurvivesNewContexts()
    {
        await using var db = await KeyTestDatabase.CreateAsync();
        var issued = await db.Store.IssueAsync("desktop", null);
        Assert.StartsWith("sk-aigw-", issued.ApiKey);
        Assert.Null(issued.Key.LastUsedAt);
        Assert.Null(issued.Key.LastUsedIp);
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var row = await ctx.Keys.SingleAsync();
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(issued.ApiKey)), row.TokenHash);
            Assert.DoesNotContain(issued.ApiKey, System.Text.Json.JsonSerializer.Serialize(row));
        }
        Assert.NotNull(await db.Store.AuthenticateAsync(issued.ApiKey));
        Assert.Null(await db.Store.AuthenticateAsync(issued.ApiKey[..^1] + (issued.ApiKey[^1] == 'x' ? "y" : "x")));
        Assert.True(await db.Store.RevokeAsync(issued.Key.Id));
        Assert.True(await db.Store.RevokeAsync(issued.Key.Id));
        Assert.False(await db.Store.RevokeAsync(Guid.NewGuid()));
        Assert.Null(await db.Store.AuthenticateAsync(issued.ApiKey));
        var listed = Assert.Single(await db.Store.ListAsync(50, 0));
        Assert.NotNull(listed.RevokedAt);
        Assert.DoesNotContain("tokenHash", System.Text.Json.JsonSerializer.Serialize(listed));
    }

    [Fact]
    public async Task ExpirationUsesUtcClockAndRejectsBoundary()
    {
        await using var db = await KeyTestDatabase.CreateAsync();
        var expires = db.Clock.GetUtcNow().AddMinutes(1);
        var key = await db.Store.IssueAsync("expiring", expires.ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(DateTimeKind.Utc, key.Key.ExpiresAt!.Value.Kind);
        Assert.NotNull(await db.Store.AuthenticateAsync(key.ApiKey));
        db.Clock.UtcNow = expires;
        Assert.Null(await db.Store.AuthenticateAsync(key.ApiKey));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.IssueAsync("past", expires));
    }

    [Fact]
    public async Task LastUseIsPairedMonotonicAndNeverRestoresRevokedKey()
    {
        await using var db = await KeyTestDatabase.CreateAsync();
        var key = await db.Store.IssueAsync("usage", null);
        var first = db.Clock.GetUtcNow().UtcDateTime;
        await db.Store.RecordUseAsync(key.Key.Id, first.AddMinutes(1), "2001:db8::1");
        await db.Store.RecordUseAsync(key.Key.Id, first, "192.0.2.1");
        var paired = Assert.Single(await db.Store.ListAsync(50, 0));
        Assert.Equal(first.AddMinutes(1),paired.LastUsedAt);
        Assert.Equal("2001:db8::1",paired.LastUsedIp);
        await db.Store.RevokeAsync(key.Key.Id);
        await db.Store.RecordUseAsync(key.Key.Id, first.AddMinutes(2), null);
        var view = Assert.Single(await db.Store.ListAsync(50, 0));
        Assert.Equal(first.AddMinutes(2), view.LastUsedAt);
        Assert.Null(view.LastUsedIp);
        Assert.NotNull(view.RevokedAt);
        Assert.Null(await db.Store.AuthenticateAsync(key.ApiKey));
    }

    [Fact]
    public async Task LockedFileReturnsErrorWithinConfiguredTimeout()
    {
        var path=Path.Combine(Path.GetTempPath(),"gateway-lock-"+Guid.NewGuid().ToString("N")+".db");
        try
        {
            var factory=new KeyContextFactory(new DbContextOptionsBuilder<GatewayKeyDbContext>().UseSqlite($"Data Source={path};Pooling=False;Default Timeout=1",o=>o.CommandTimeout(1)).Options);
            await using(var db=factory.CreateDbContext()) await db.Database.MigrateAsync();
            var store=new GatewayKeyStore(factory,TimeProvider.System,NullLogger<GatewayKeyStore>.Instance);
            var key=await store.IssueAsync("locked",null);
            await using var blocker=new SqliteConnection($"Data Source={path};Pooling=False");await blocker.OpenAsync();
            await using var transaction=blocker.BeginTransaction();
            using var command=blocker.CreateCommand();command.Transaction=transaction;command.CommandText="UPDATE gateway_api_keys SET name='locked writer'";await command.ExecuteNonQueryAsync();
            Assert.NotNull(await store.AuthenticateAsync(key.ApiKey)); // WAL permits readers while one writer is active.
            var timer=System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<SqliteException>(()=>store.RecordUseAsync(key.Key.Id,DateTime.UtcNow,"192.0.2.1"));
            Assert.True(timer.Elapsed<TimeSpan.FromSeconds(5));
            await transaction.RollbackAsync();
        }
        finally { foreach(var suffix in new[]{"","-wal","-shm"}) File.Delete(path+suffix); }
    }

    [Fact]
    public async Task FileDatabaseMigratesRepeatedlyAndPreservesKeysAcrossReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "gateway-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new KeyContextFactory(new DbContextOptionsBuilder<GatewayKeyDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await using (var ctx = factory.CreateDbContext()) { await ctx.Database.MigrateAsync(); await ctx.Database.MigrateAsync(); }
            var store = new GatewayKeyStore(factory, TimeProvider.System, NullLogger<GatewayKeyStore>.Instance);
            var key = await store.IssueAsync("persisted", null);
            var reopened = new GatewayKeyStore(factory, TimeProvider.System, NullLogger<GatewayKeyStore>.Instance);
            Assert.NotNull(await reopened.AuthenticateAsync(key.ApiKey));
            await reopened.RevokeAsync(key.Key.Id);
            Assert.Null(await store.AuthenticateAsync(key.ApiKey));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task InvalidTokensDoNotNeedAWorkingDatabase()
    {
        await using var db = await KeyTestDatabase.CreateAsync();
        await using (var ctx = db.Factory.CreateDbContext()) await ctx.Database.ExecuteSqlRawAsync("DROP TABLE gateway_api_keys");
        Assert.Null(await db.Store.AuthenticateAsync("sk-aigw-malformed"));
        Assert.Null(await db.Store.AuthenticateAsync("SK-AIGW-malformed"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Store.AuthenticateAsync("sk-aigw-" + Guid.NewGuid().ToString("N") + "." + new string('a',43)));
    }
}

internal sealed class KeyContextFactory(DbContextOptions<GatewayKeyDbContext> options) : IDbContextFactory<GatewayKeyDbContext>
{
    public GatewayKeyDbContext CreateDbContext() => new(options);
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class KeyTestDatabase(SqliteConnection connection, KeyContextFactory factory) : IAsyncDisposable
{
    public KeyContextFactory Factory { get; } = factory;
    public TestClock Clock { get; } = new();
    public GatewayKeyStore Store => new(Factory, Clock, NullLogger<GatewayKeyStore>.Instance);
    public static async Task<KeyTestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new KeyContextFactory(new DbContextOptionsBuilder<GatewayKeyDbContext>().UseSqlite(connection).Options);
        await using var ctx = factory.CreateDbContext();
        await ctx.Database.MigrateAsync();
        return new(connection, factory);
    }
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
