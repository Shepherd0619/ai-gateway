using System.Text.Json;
using AiGateway.Configuration;
using AiGateway.Discovery;
using Xunit;

namespace AiGateway.Tests;

public sealed class ModelDiscoveryResponseTests
{
    [Fact]
    public void OpenAiResponse_UsesRequiredFieldsAndConfiguredMetadata()
    {
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var response = ModelDiscoveryEndpoints.ToOpenAiResponse([
            new ModelDiscoveryModel { Id = "gpt-test", CreatedAt = createdAt, OwnedBy = "example" },
        ]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var root = json.RootElement;
        var model = Assert.Single(root.GetProperty("data").EnumerateArray());

        Assert.Equal("list", root.GetProperty("object").GetString());
        Assert.Equal("gpt-test", model.GetProperty("id").GetString());
        Assert.Equal("model", model.GetProperty("object").GetString());
        Assert.Equal(1_700_000_000, model.GetProperty("created").GetInt64());
        Assert.Equal("example", model.GetProperty("owned_by").GetString());
    }

    [Fact]
    public void AnthropicResponse_UsesRequiredFieldsAndPaginationMetadata()
    {
        var createdAt = DateTimeOffset.Parse("2025-05-14T00:00:00Z");
        var response = ModelDiscoveryEndpoints.ToAnthropicResponse([
            new ModelDiscoveryModel { Id = "claude-x", DisplayName = "Claude X", CreatedAt = createdAt },
        ]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var root = json.RootElement;
        var model = Assert.Single(root.GetProperty("data").EnumerateArray());

        Assert.Equal("claude-x", model.GetProperty("id").GetString());
        Assert.Equal("model", model.GetProperty("type").GetString());
        Assert.Equal("Claude X", model.GetProperty("display_name").GetString());
        Assert.Equal(createdAt, model.GetProperty("created_at").GetDateTimeOffset());
        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Equal("claude-x", root.GetProperty("first_id").GetString());
        Assert.Equal("claude-x", root.GetProperty("last_id").GetString());
    }

    [Fact]
    public void AnthropicResponse_UsesIdAndOmitsUnsetCreationDate()
    {
        var response = ModelDiscoveryEndpoints.ToAnthropicResponse([
            new ModelDiscoveryModel { Id = "model-x" },
        ]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var model = Assert.Single(json.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal("model-x", model.GetProperty("display_name").GetString());
        Assert.False(model.TryGetProperty("created_at", out _));
    }
}
