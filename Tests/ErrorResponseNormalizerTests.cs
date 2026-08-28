using System.Text;
using System.Text.Json;
using AiGateway.Proxy;
using Xunit;

namespace AiGateway.Tests;

public sealed class ErrorResponseNormalizerTests
{
    [Fact]
    public void Promotes_nested_provider_message_when_top_level_is_generic()
    {
        var input = Encoding.UTF8.GetBytes("""
            {"type":"error","error":{"type":"invalid_request_error","message":"Provider returned error","error_type":"invalid_request"},"request_id":"gen-123","metadata":{"raw":"{\"error\":{\"message\":\"The image data is invalid.\",\"type\":\"invalid_request_error\"}}","provider_name":"OpenAI"}}
            """);
        var original = input.ToArray();

        var output = ErrorResponseNormalizer.Normalize(input);
        using var doc = JsonDocument.Parse(output);

        Assert.Equal("The image data is invalid.", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("gen-123", doc.RootElement.GetProperty("request_id").GetString());
        Assert.Equal("OpenAI", doc.RootElement.GetProperty("metadata").GetProperty("provider_name").GetString());
        Assert.Equal(original, input);
    }

    [Fact]
    public void Preserves_specific_top_level_message()
    {
        var input = Encoding.UTF8.GetBytes("""
            {"error":{"type":"invalid_request_error","message":"The request is invalid."},"metadata":{"raw":"{\"error\":{\"message\":\"Provider detail\"}}"}}
            """);

        var output = ErrorResponseNormalizer.Normalize(input);

        using var doc = JsonDocument.Parse(output);
        Assert.Equal("The request is invalid.", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"metadata\":{}}")]
    [InlineData("{\"error\":{\"message\":\"Provider returned error\"},\"metadata\":{\"raw\":\"not json\"}}")]
    [InlineData("{\"error\":{\"message\":\"Provider returned error\"},\"metadata\":{\"raw\":\"{\\\"error\\\":{}}\"}}")]
    [InlineData("not json")]
    public void Returns_original_bytes_when_nested_provider_message_is_unavailable(string body)
    {
        var input = Encoding.UTF8.GetBytes(body);

        Assert.Same(input, ErrorResponseNormalizer.Normalize(input));
    }

    [Fact]
    public void Returns_original_bytes_for_success_payloads()
    {
        var input = Encoding.UTF8.GetBytes("""{"type":"message","metadata":{"raw":"{\"error\":{\"message\":\"detail\"}}"}}""");

        Assert.Same(input, ErrorResponseNormalizer.Normalize(input, isErrorResponse: false));
    }

    [Fact]
    public void Adds_message_when_top_level_error_message_is_missing()
    {
        var input = Encoding.UTF8.GetBytes("""{"error":{"type":"invalid_request_error"},"metadata":{"raw":"{\"error\":{\"message\":\"Provider detail\"}}"}}""");

        using var doc = JsonDocument.Parse(ErrorResponseNormalizer.Normalize(input));

        Assert.Equal("Provider detail", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
    }
}
