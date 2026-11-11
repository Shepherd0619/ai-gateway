using AiGateway.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiGateway.Tests;

public sealed class GatewayKeyDatabaseTests
{
    [Fact]
    public async Task EnabledStartupMigratesFileAndCanRunAgainWithoutPendingModelChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "gateway-init-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["GatewayKeys:Enabled"]="true",["ConnectionStrings:GatewayKeys"]=$"Data Source={path};Pooling=False" }).Build();
            var services = new ServiceCollection().AddLogging();services.AddGatewayKeys(config);
            await using var provider=services.BuildServiceProvider();
            await GatewayKeyDatabase.InitializeAsync(provider);
            await GatewayKeyDatabase.InitializeAsync(provider);
            await using var db=provider.GetRequiredService<IDbContextFactory<GatewayKeyDbContext>>().CreateDbContext();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
            Assert.NotEmpty((await provider.GetRequiredService<GatewayKeyStore>().IssueAsync("startup",null)).ApiKey);
        }
        finally { foreach(var suffix in new[]{"","-wal","-shm"}) File.Delete(path+suffix); }
    }

    [Fact]
    public async Task UnwritableTargetFailsStartupWithSanitizedError()
    {
        // A directory cannot be opened as a database, even when the test process is privileged.
        var path=Path.Combine(Path.GetTempPath(),"gateway-directory-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["GatewayKeys:Enabled"]="true",["ConnectionStrings:GatewayKeys"]=$"Data Source={path};Pooling=False" }).Build();
            var services=new ServiceCollection().AddLogging();services.AddGatewayKeys(config);
            await using var provider=services.BuildServiceProvider();
            var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>GatewayKeyDatabase.InitializeAsync(provider));
            Assert.DoesNotContain(path,error.ToString());
            Assert.Null(error.InnerException);
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public async Task DisabledModeDoesNotCreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["ConnectionStrings:GatewayKeys"] = $"Data Source={path}" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddGatewayKeys(config);
        await using var provider = services.BuildServiceProvider();
        await GatewayKeyDatabase.InitializeAsync(provider);
        Assert.False(File.Exists(path));
        Assert.Null(provider.GetService<GatewayKeyStore>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=temporary;Mode=Memory")]
    [InlineData("Data Source=")]
    [InlineData("invalid=secret-connection-string")]
    public void EnabledModeRejectsMissingInvalidOrEphemeralConnections(string? connection)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["GatewayKeys:Enabled"] = "true", ["ConnectionStrings:GatewayKeys"] = connection }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddGatewayKeys(config));
        Assert.DoesNotContain("secret-connection-string", error.ToString());
    }
}
