using System.Text.Json;
using AiGateway.Configuration;
using static AiGateway.Proxy.AnthropicTranslator;

namespace AiGateway.Proxy;

// Claude Code keeps the complete catalog in each request. ToolSearch runs on
// the client; references in its results activate the corresponding schemas.
internal static class DeferredToolCatalog
{
    internal static List<JsonElement>? Select(JsonElement root, JsonElement messages, ReasoningFormat format)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.EnumerateArray())
        {
            if (Text(message, "role") != "user" || !message.TryGetProperty("content", out var blocks)
                || blocks.ValueKind != JsonValueKind.Array) continue;
            foreach (var block in blocks.EnumerateArray())
            {
                if (Text(block, "type") != "tool_result" || !block.TryGetProperty("content", out var parts)
                    || parts.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (Text(part, "type") != "tool_reference") continue;
                    var reference = ReferenceName(part);
                    if (!string.Equals(reference, "web_search", StringComparison.Ordinal))
                        references.Add(reference);
                }
            }
        }
        if (!root.TryGetProperty("tools", out var tools))
        {
            Require(references.Count == 0, "Tool references require a tools catalog.");
            return null;
        }
        Require(tools.ValueKind == JsonValueKind.Array, "tools must be an array.");
        var forcedName = root.TryGetProperty("tool_choice", out var choice) && Text(choice, "type") == "tool"
            ? Text(choice, "name") : null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<JsonElement>();
        foreach (var tool in tools.EnumerateArray())
        {
            var type = Text(tool, "type");
            Require(type is null or "custom", $"Anthropic server tool '{type}' cannot be translated to Chat Completions. Use a client function tool instead.");
            var name = Text(tool, "name");
            Require(!string.IsNullOrWhiteSpace(name), "Tool name is required.");
            Require(names.Add(name!), $"Duplicate tool name '{name}'.");
            Require(tool.TryGetProperty("input_schema", out var schema) && schema.ValueKind == JsonValueKind.Object, "Tool input_schema must be an object.");
            var deferred = false;
            if (tool.TryGetProperty("defer_loading", out var flag))
            {
                Require(flag.ValueKind is JsonValueKind.True or JsonValueKind.False, "Tool defer_loading must be boolean.");
                deferred = flag.GetBoolean();
            }
            if (!deferred || references.Contains(name!) || name == forcedName) selected.Add(tool);
        }
        foreach (var reference in references)
            Require(names.Contains(reference), $"Tool reference '{reference}' is missing from the tools catalog.");
        Require(format != ReasoningFormat.OpenAI || selected.Count <= 128,
            $"OpenAI Chat Completions accepts at most 128 active tools; this request has {selected.Count} active tools out of {names.Count}. " +
            "Enable MCP tool deferral in Claude Code or disable unused MCP servers. No tools were truncated.");
        return selected;
    }

    internal static string ReferenceName(JsonElement part)
    {
        var name = Text(part, "tool_name");
        Require(!string.IsNullOrWhiteSpace(name), "tool_reference.tool_name is required.");
        return name!;
    }
}
