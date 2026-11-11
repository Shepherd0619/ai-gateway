using System.Net;
using AiGateway.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class ClientAuthenticationTests
{
    internal static ClientAuthentication Auth(bool enabled, GatewayKeyStore? store = null) =>
        new(Options.Create(new GatewayKeyOptions { Enabled = enabled }), NullLogger<ClientAuthentication>.Instance, store);
    internal static DefaultHttpContext Context(string? key = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.15");
        if (key is not null) ctx.Request.Headers["x-api-key"] = key;
        return ctx;
    }

    [Theory]
    [InlineData("sk-aigw-malformed")]
    [InlineData("SK-AIGW-malformed")]
    public async Task DisabledGatewayKeysRejectReservedTokensEvenForAnonymousDiscovery(string token)
    {
        var ctx = Context(token);
        Assert.Null(await Auth(false).AuthenticateAsync(ctx, allowAnonymous: true));
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ConflictingOrMultipleCredentialsAreRejected()
    {
        var ctx = Context("first"); ctx.Request.Headers.Authorization = "Bearer second";
        Assert.Null(await Auth(false).AuthenticateAsync(ctx));
        Assert.Equal(400, ctx.Response.StatusCode);
        ctx = Context(); ctx.Request.Headers["x-api-key"] = new[] { "same", "same" };
        Assert.Null(await Auth(false).AuthenticateAsync(ctx));
        Assert.Equal(400, ctx.Response.StatusCode);
        ctx = Context("same"); ctx.Request.Headers.Authorization = "Bearer same";
        Assert.Equal("same", (await Auth(false).AuthenticateAsync(ctx))!.ByokKey);
    }

    [Fact]
    public async Task SuccessfulAuthenticationRecordsUseOnceAndNormalizesMappedIp()
    {
        await using var db = await KeyTestDatabase.CreateAsync();
        var issued = await db.Store.IssueAsync("client", null);
        var ctx = Context(issued.ApiKey);
        var auth = Auth(true, db.Store);
        Assert.True((await auth.AuthenticateAsync(ctx))!.IsGateway);
        db.Clock.UtcNow = db.Clock.UtcNow.AddMinutes(1);
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.99");
        Assert.True((await auth.AuthenticateAsync(ctx))!.IsGateway);
        var view = Assert.Single(await db.Store.ListAsync(50, 0));
        Assert.Equal("192.0.2.15", view.LastUsedIp);
        Assert.Equal(db.Clock.UtcNow.AddMinutes(-1).UtcDateTime, view.LastUsedAt);
    }

    [Fact]
    public async Task DatabaseFailureIs503ForGatewayKeysButByokStillWorks()
    {
        await using var db = await KeyTestDatabase.CreateAsync();
        var key = await db.Store.IssueAsync("client", null);
        await using (var ctx = db.Factory.CreateDbContext()) await ctx.Database.ExecuteSqlRawAsync("DROP TABLE gateway_api_keys");
        var auth = Auth(true, db.Store);
        var gateway = Context(key.ApiKey);
        Assert.Null(await auth.AuthenticateAsync(gateway));
        Assert.Equal(503, gateway.Response.StatusCode);
        var byok = Context("provider-key");
        Assert.Equal("provider-key", (await auth.AuthenticateAsync(byok))!.ByokKey);
    }

    [Fact]
    public async Task UseTrackingFailureDoesNotRejectValidAuthentication()
    {
        await using var db = await KeyTestDatabase.CreateAsync();
        var key = await db.Store.IssueAsync("client", null);
        await using (var ctx = db.Factory.CreateDbContext())
            await ctx.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_usage BEFORE UPDATE OF last_used_at ON gateway_api_keys BEGIN SELECT RAISE(FAIL, 'usage blocked'); END");
        var context = Context(key.ApiKey);
        Assert.True((await Auth(true, db.Store).AuthenticateAsync(context))!.IsGateway);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Null(Assert.Single(await db.Store.ListAsync(50, 0)).LastUsedAt);
    }

    [Fact]
    public async Task InvalidExpiredAndRevokedKeysDoNotRecordUse()
    {
        await using var db=await KeyTestDatabase.CreateAsync();
        var issued=await db.Store.IssueAsync("client",db.Clock.UtcNow.AddMinutes(1));
        var auth=Auth(true,db.Store);
        var invalid=issued.ApiKey[..^1]+(issued.ApiKey[^1]=='x'?"y":"x");
        Assert.Null(await auth.AuthenticateAsync(Context(invalid)));
        db.Clock.UtcNow=db.Clock.UtcNow.AddMinutes(1);
        Assert.Null(await auth.AuthenticateAsync(Context(issued.ApiKey)));
        db.Clock.UtcNow=db.Clock.UtcNow.AddMinutes(-1);
        await db.Store.RevokeAsync(issued.Key.Id);
        Assert.Null(await auth.AuthenticateAsync(Context(issued.ApiKey)));
        var view=Assert.Single(await db.Store.ListAsync(50,0));
        Assert.Null(view.LastUsedAt);Assert.Null(view.LastUsedIp);
    }
}
