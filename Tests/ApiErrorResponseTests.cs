using System.Text.Json;
using AiGateway.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AiGateway.Tests;

public sealed class ApiErrorResponseTests
{
    private const string Message = "Invalid \"model\"\n路径\\test\t\u0001";

    [Theory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(404, "not_found_error")]
    [InlineData(413, "request_too_large")]
    [InlineData(429, "rate_limit_error")]
    [InlineData(529, "overloaded_error")]
    [InlineData(503, "api_error")]
    [InlineData(504, "api_error")]
    public void CreateAnthropic_UsesProtocolErrorTypes(int status, string type)
    {
        using var doc = JsonDocument.Parse(ApiErrorResponse.CreateAnthropic(status, Message));
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(type, doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(Message, doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_PreservesGenericEnvelopeAndEscapesMessage(bool noStore)
    {
        var ctx = Context();
        await ApiErrorResponse.WriteAsync(ctx, 503, "provider_key_not_configured", Message, noStore);

        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Equal("application/json", ctx.Response.ContentType);
        Assert.Equal(noStore ? "no-store" : "", ctx.Response.Headers.CacheControl.ToString());
        using var doc = JsonDocument.Parse(((MemoryStream)ctx.Response.Body).ToArray());
        Assert.False(doc.RootElement.TryGetProperty("type", out _));
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("provider_key_not_configured", error.GetProperty("type").GetString());
        Assert.Equal(Message, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WriteAnthropicAsync_PreservesEnvelopeAndExistingResponseHeaders()
    {
        var ctx = Context();
        ctx.Response.Headers["request-id"] = "trace-test";
        await ApiErrorResponse.WriteAnthropicAsync(ctx, 400, Message);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("application/json", ctx.Response.ContentType);
        Assert.Equal("trace-test", ctx.Response.Headers["request-id"].ToString());
        Assert.False(ctx.Response.Headers.ContainsKey("Cache-Control"));
        using var doc = JsonDocument.Parse(((MemoryStream)ctx.Response.Body).ToArray());
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(Message, doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }
}
