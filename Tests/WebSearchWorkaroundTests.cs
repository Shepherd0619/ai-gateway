using System.Text.Json;
using AiGateway.Proxy;
using Xunit;

namespace AiGateway.Tests;

public sealed class WebSearchWorkaroundTests
{
    [Fact]
    public void Removes_tool_choice_when_web_search_is_present()
    {
        using var document = JsonDocument.Parse("""
            {
              "model":"openai/gpt-5.6-luna",
              "tools":[{"type":"web_search_20250305","name":"web_search"}],
              "tool_choice":{"type":"tool","name":"web_search"}
            }
            """);

        var output = Serialize(document.RootElement);

        using var result = JsonDocument.Parse(output);
        Assert.False(result.RootElement.TryGetProperty("tool_choice", out _));
        Assert.Equal("web_search_20250305", result.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Preserves_tool_choice_without_web_search()
    {
        using var document = JsonDocument.Parse("""
            {
              "model":"openai/gpt-5.6-luna",
              "tools":[{"name":"get_weather","input_schema":{"type":"object"}}],
              "tool_choice":{"type":"tool","name":"get_weather"}
            }
            """);

        using var result = JsonDocument.Parse(Serialize(document.RootElement));
        Assert.Equal("get_weather", result.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
    }

    [Fact]
    public void Preserves_web_search_when_tool_choice_is_absent()
    {
        using var document = JsonDocument.Parse("""
            {
              "tools":[{"type":"web_search_20250305","name":"web_search"}]
            }
            """);

        using var result = JsonDocument.Parse(Serialize(document.RootElement));
        Assert.Equal("web_search", result.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    private static byte[] Serialize(JsonElement root)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
            ProxyHandler.WriteTransformed(writer, root, newModel: null, stripBillingHeader: false, stripToolChoice: ProxyHandler.HasWebSearchTool(root) && root.TryGetProperty("tool_choice", out _));
        return output.ToArray();
    }
}
