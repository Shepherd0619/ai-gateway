using System.Text;
using System.Text.Json;
using AiGateway.Configuration;
using AiGateway.Proxy;
using Xunit;

namespace AiGateway.Tests;

public sealed class DeferredToolTests
{
    private static byte[] CatalogRequest(bool deferred, string? reference = null, string? requiredTool = null)
    {
        var tools = Enumerable.Range(0, 298).Select(i => new
        {
            name = i == 0 ? "ToolSearch" : $"tool_{i}",
            description = $"Tool {i}",
            input_schema = new { type = "object" },
            defer_loading = deferred && i > 0
        });
        var messages = new List<object> { new { role = "user", content = "Find the relevant tool" } };
        if (reference is not null)
        {
            messages.Add(new { role = "assistant", content = new[] { new { type = "tool_use", id = "search-1", name = "ToolSearch", input = new { query = "Find a tool" } } } });
            messages.Add(new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "search-1", content = new[] { new { type = "tool_reference", tool_name = reference } } } } });
        }
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "m", max_tokens = 1024, tools, messages,
            tool_choice = requiredTool is null ? (object)new { type = "auto" } : new { type = "tool", name = requiredTool }
        });
    }

    [Fact]
    public void LargeDeferredCatalog_SendsOnlyUpfrontTools()
    {
        using var doc = JsonDocument.Parse(AnthropicTranslator.TranslateRequest(CatalogRequest(true)));
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Single(tools.EnumerateArray());
        Assert.Equal("ToolSearch", tools[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void SearchResult_LoadsReferencedToolWithoutLosingCallHistory()
    {
        using var doc = JsonDocument.Parse(AnthropicTranslator.TranslateRequest(CatalogRequest(true, "tool_297")));
        var root = doc.RootElement;
        Assert.Equal(new[] { "ToolSearch", "tool_297" }, root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()));
        var tool = root.GetProperty("tools")[1].GetProperty("function");
        Assert.Equal("Tool 297", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal("ToolSearch", messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("search-1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Contains("tool_297", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public void NamedToolChoice_LoadsDeferredTool()
    {
        using var doc = JsonDocument.Parse(AnthropicTranslator.TranslateRequest(CatalogRequest(true, requiredTool: "tool_200")));
        Assert.Contains(doc.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "tool_200");
    }

    [Fact]
    public void UnknownToolReference_FailsExplicitly()
    {
        var error = Assert.Throws<TranslationException>(() => AnthropicTranslator.TranslateRequest(CatalogRequest(true, "unknown_tool")));
        Assert.Contains("unknown_tool", error.Message);
    }

    [Fact]
    public void TooManyUpfrontTools_AreNotSilentlyTruncated()
    {
        var error = Assert.Throws<TranslationException>(() => AnthropicTranslator.TranslateRequest(CatalogRequest(false)));
        Assert.Contains("298", error.Message);
        Assert.Contains("128", error.Message);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    public void OpenAiToolLimit_IsEnforcedAtBoundary(int count)
    {
        using var source = JsonDocument.Parse(CatalogRequest(false));
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "m", max_tokens = 1024,
            tools = source.RootElement.GetProperty("tools").EnumerateArray().Take(count).ToArray(),
            messages = new[] { new { role = "user", content = "hi" } }
        });
        if (count > 128) Assert.Throws<TranslationException>(() => AnthropicTranslator.TranslateRequest(request));
        else
        {
            using var doc = JsonDocument.Parse(AnthropicTranslator.TranslateRequest(request));
            Assert.Equal(128, doc.RootElement.GetProperty("tools").GetArrayLength());
        }
    }

    [Fact]
    public void OpenRouter_DoesNotInheritOfficialOpenAiToolLimit()
    {
        using var doc = JsonDocument.Parse(AnthropicTranslator.TranslateRequest(CatalogRequest(false), ReasoningFormat.OpenRouter));
        Assert.Equal(298, doc.RootElement.GetProperty("tools").GetArrayLength());
    }
}
