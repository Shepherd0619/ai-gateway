using Microsoft.AspNetCore.Http;
using System.Net.Http;
using Xunit;
using AiGateway.Proxy;

namespace AiGateway.Tests;

public sealed class ClientIdentityHeadersTests
{
    [Fact]
    public void ForwardClientIdentityHeaders_CopiesIdentityHeadersWithoutOverwritingGatewayHeaders()
    {
        var clientContext = new DefaultHttpContext();
        clientContext.Request.Headers.UserAgent = "Claude Code/1.2.3";
        clientContext.Request.Headers["X-Title"] = "Claude Code";
        clientContext.Request.Headers["X-OpenRouter-Title"] = "Claude Code";
        clientContext.Request.Headers["HTTP-Referer"] = "https://claude.ai/code";
        clientContext.Request.Headers["X-Client-Name"] = "claude-code";
        clientContext.Request.Headers["Authorization"] = "Bearer client-token";

        using var upstream = new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/messages")
        {
            Content = new ByteArrayContent([])
        };
        upstream.Headers.TryAddWithoutValidation("Authorization", "Bearer gateway-token");
        upstream.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        ProxyHandler.ForwardClientIdentityHeaders(clientContext.Request, upstream);

        Assert.Equal("Claude Code/1.2.3", upstream.Headers.UserAgent.ToString());
        Assert.Equal("Claude Code", upstream.Headers.GetValues("X-Title").Single());
        Assert.Equal("Claude Code", upstream.Headers.GetValues("X-OpenRouter-Title").Single());
        Assert.Equal("https://claude.ai/code", upstream.Headers.GetValues("HTTP-Referer").Single());
        Assert.Equal("claude-code", upstream.Headers.GetValues("X-Client-Name").Single());
        Assert.Equal("Bearer gateway-token", upstream.Headers.GetValues("Authorization").Single());
        Assert.Equal("2023-06-01", upstream.Headers.GetValues("anthropic-version").Single());
    }
}
