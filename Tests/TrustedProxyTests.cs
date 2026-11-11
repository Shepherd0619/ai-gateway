using System.Net;
using AiGateway.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiGateway.Tests;

public sealed class TrustedProxyTests
{
    [Theory]
    [InlineData("10.0.0.2", "198.51.100.23")]
    [InlineData("::ffff:10.0.0.2", "198.51.100.23")]
    [InlineData("10.0.0.3", "10.0.0.3")]
    public async Task OnlyConfiguredImmediateProxyCanSetClientIp(string peer, string expected)
    {
        var config = Config("10.0.0.2");
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseTrustedForwarding(config);
        app.Run(_ => Task.CompletedTask);
        var ctx = ClientAuthenticationTests.Context();
        ctx.RequestServices = provider;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        ctx.Request.Headers["X-Forwarded-For"] = "192.0.2.99, 198.51.100.23";
        await app.Build()(ctx);
        Assert.Equal(expected, ctx.Connection.RemoteIpAddress!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-ip")]
    public void EnabledForwardingRequiresExplicitValidProxy(string? proxy)
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => new ApplicationBuilder(provider).UseTrustedForwarding(Config(proxy)));
    }

    private static IConfiguration Config(string? proxy) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:KnownProxies:0"] = proxy }).Build();
}
