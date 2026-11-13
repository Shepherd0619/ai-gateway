using System.Text.Json;

namespace AiGateway.Proxy;

internal static class AnthropicServerToolFallback
{
    private const string WebSearchGuidance =
        "The built-in web_search server tool is unavailable in this gateway. Use the Firecrawl MCP search tools available in the supplied tool list for web research. If none are available, answer using only the conversation and supplied data, and state that you could not perform a live search. Continue with the user's original request.";

    internal static byte[] RemoveWebSearch(byte[] body, out bool removed)
    {
        removed = false;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
            return body;

        var retainedTools = new List<JsonElement>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (IsWebSearch(tool))
                removed = true;
            else
                retainedTools.Add(tool);
        }

        if (!removed)
            return body;

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("tools"))
                {
                    if (retainedTools.Count == 0) continue;
                    writer.WritePropertyName("tools");
                    writer.WriteStartArray();
                    foreach (var tool in retainedTools) tool.WriteTo(writer);
                    writer.WriteEndArray();
                }
                else if (property.NameEquals("messages"))
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("role", "system");
                    writer.WriteString("content", WebSearchGuidance);
                    writer.WriteEndObject();
                    foreach (var message in messages.EnumerateArray()) message.WriteTo(writer);
                    writer.WriteEndArray();
                }
                else if (property.NameEquals("tool_choice") && ShouldRemoveToolChoice(property.Value, retainedTools.Count))
                {
                    // Let the model choose an available client tool instead.
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    internal static bool IsWebSearch(JsonElement tool)
    {
        var type = AnthropicTranslator.Text(tool, "type");
        return type is not null && type.StartsWith("web_search_", StringComparison.Ordinal);
    }

    private static bool ShouldRemoveToolChoice(JsonElement choice, int retainedToolCount)
    {
        if (choice.ValueKind != JsonValueKind.Object)
            return false;

        if (AnthropicTranslator.Text(choice, "type") == "tool"
            && AnthropicTranslator.Text(choice, "name") == "web_search")
            return true;

        return retainedToolCount == 0 && AnthropicTranslator.Text(choice, "type") == "any";
    }
}
