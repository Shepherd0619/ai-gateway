using System.Text;
using System.Text.Json;
using static AiGateway.Proxy.AnthropicTranslator;

namespace AiGateway.Proxy;

internal static class AnthropicResponsesTranslator
{
    private const string SignaturePrefix = "gateway.responses.v1:";

    internal static byte[] TranslateRequest(byte[] body) => TranslateRequest(body, out _);

    internal static byte[] TranslateRequest(byte[] body, out string[] stopSequences)
    {
        // Reuse Messages validation, tool deferral, image lifting and tool-result
        // ordering. This is a local intermediate representation, never an API call.
        using var chat = JsonDocument.Parse(AnthropicTranslator.TranslateRequest(body));
        using var original = JsonDocument.Parse(body);
        var root = chat.RootElement;
        stopSequences = [];
        if (root.TryGetProperty("stop", out var stop))
        {
            Require(stop.ValueKind == JsonValueKind.Array, "stop_sequences must be an array of nonempty strings.");
            Require(stop.EnumerateArray().All(s => s.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(s.GetString())),
                "stop_sequences must be an array of nonempty strings.");
            stopSequences = stop.EnumerateArray().Select(s => s.GetString()!).ToArray();
        }
        var assistants = new Queue<JsonElement>(original.RootElement.GetProperty("messages").EnumerateArray().Where(m => Text(m, "role") == "assistant"));
        return Json(w =>
        {
            w.WriteStartObject(); Copy(w, root, "model"); Copy(w, root, "max_completion_tokens", "max_output_tokens");
            Copy(w, root, "stream"); w.WriteBoolean("store", false);
            w.WritePropertyName("include"); w.WriteStartArray(); w.WriteStringValue("reasoning.encrypted_content"); w.WriteEndArray();
            var effort = Text(root, "reasoning_effort");
            if (effort is not null)
            {
                w.WritePropertyName("reasoning"); w.WriteStartObject(); w.WriteString("effort", effort);
                if (effort != "none") w.WriteString("summary", "auto");
                w.WriteEndObject();
            }
            // Reasoning models commonly reject sampling controls when reasoning is on.
            if (effort is null or "none") { Copy(w, root, "temperature"); Copy(w, root, "top_p"); }
            Copy(w, root, "parallel_tool_calls");
            w.WritePropertyName("input"); w.WriteStartArray();
            foreach (var message in root.GetProperty("messages").EnumerateArray())
            {
                var role = Text(message, "role")!;
                if (role == "tool")
                {
                    w.WriteStartObject(); w.WriteString("type", "function_call_output"); Copy(w, message, "tool_call_id", "call_id");
                    Copy(w, message, "content", "output"); w.WriteEndObject(); continue;
                }
                if (role == "assistant") WriteReasoningHistory(w, assistants.Dequeue());
                if (message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    w.WriteStartObject(); w.WriteString("role", role); w.WritePropertyName("content");
                    if (content.ValueKind == JsonValueKind.String) content.WriteTo(w);
                    else
                    {
                        w.WriteStartArray();
                        foreach (var part in content.EnumerateArray())
                        {
                            w.WriteStartObject();
                            if (Text(part, "type") == "image_url")
                            {
                                Require(role != "assistant", "Responses does not accept assistant image history.");
                                w.WriteString("type", "input_image"); w.WriteString("image_url", part.GetProperty("image_url").GetProperty("url").GetString());
                            }
                            else { w.WriteString("type", role == "assistant" ? "output_text" : "input_text"); Copy(w, part, "text"); }
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                if (message.TryGetProperty("tool_calls", out var calls))
                    foreach (var call in calls.EnumerateArray())
                    {
                        w.WriteStartObject(); w.WriteString("type", "function_call"); Copy(w, call, "id", "call_id");
                        var function = call.GetProperty("function"); Copy(w, function, "name"); Copy(w, function, "arguments"); w.WriteEndObject();
                    }
            }
            w.WriteEndArray();
            if (root.TryGetProperty("tools", out var tools))
            {
                w.WritePropertyName("tools"); w.WriteStartArray();
                foreach (var tool in tools.EnumerateArray())
                {
                    var function = tool.GetProperty("function");
                    w.WriteStartObject(); w.WriteString("type", "function"); Copy(w, function, "name"); Copy(w, function, "description");
                    Copy(w, function, "parameters"); w.WriteBoolean("strict", false); w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (root.TryGetProperty("tool_choice", out var choice))
            {
                w.WritePropertyName("tool_choice");
                if (choice.ValueKind == JsonValueKind.String) choice.WriteTo(w);
                else { w.WriteStartObject(); w.WriteString("type", "function"); Copy(w, choice.GetProperty("function"), "name"); w.WriteEndObject(); }
            }
            w.WriteEndObject();
        });
    }

    private static void WriteReasoningHistory(Utf8JsonWriter w, JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            var signature = Text(block, "signature");
            if (Text(block, "type") != "thinking" || signature is null || !signature.StartsWith(SignaturePrefix, StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(Convert.FromBase64String(signature[SignaturePrefix.Length..]));
                Require(Text(doc.RootElement, "type") == "reasoning", "Invalid gateway reasoning signature.");
                doc.RootElement.WriteTo(w);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            { throw new TranslationException("Invalid gateway reasoning signature."); }
        }
    }

    internal static string Signature(JsonElement item) => SignaturePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(item.GetRawText()));

    internal static byte[] TranslateResponse(byte[] body, string model, IReadOnlyList<string>? stopSequences = null)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var finish = Finish(root);
        Require(root.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array, "Responses output must be an array.");
        return StopSequenceMatcher.Apply(Json(w =>
        {
            w.WriteStartObject(); WriteMessageHeader(w, Text(root, "id"), model);
            w.WritePropertyName("content"); w.WriteStartArray();
            foreach (var item in items.EnumerateArray())
            {
                switch (Text(item, "type"))
                {
                    case "reasoning":
                        var summary = item.TryGetProperty("summary", out var summaries) && summaries.ValueKind == JsonValueKind.Array
                            ? string.Join("\n", summaries.EnumerateArray().Select(s => Text(s, "text") ?? "")) : "";
                        w.WriteStartObject(); w.WriteString("type", "thinking"); w.WriteString("thinking", summary);
                        w.WriteString("signature", Signature(item)); w.WriteEndObject(); break;
                    case "message":
                        Require(item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array, "Invalid Responses message.");
                        foreach (var part in parts.EnumerateArray())
                        {
                            var text = Text(part, "type") switch
                            {
                                "output_text" => Text(part, "text"), "refusal" => Text(part, "refusal"),
                                _ => throw new TranslationException("Unsupported Responses message content.")
                            };
                            Require(text is not null, "Missing Responses message text.");
                            w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", text); w.WriteEndObject();
                        }
                        break;
                    case "function_call":
                        Require(!string.IsNullOrWhiteSpace(Text(item, "call_id")) && !string.IsNullOrWhiteSpace(Text(item, "name")), "Invalid Responses function call.");
                        using (var args = JsonDocument.Parse(Text(item, "arguments") ?? "{}"))
                        {
                            Require(args.RootElement.ValueKind == JsonValueKind.Object, "Tool arguments must be a JSON object.");
                            w.WriteStartObject(); w.WriteString("type", "tool_use"); Copy(w, item, "call_id", "id"); Copy(w, item, "name");
                            w.WritePropertyName("input"); args.RootElement.WriteTo(w); w.WriteEndObject();
                        }
                        break;
                    default: throw new TranslationException($"Unsupported Responses output item '{Text(item, "type")}'.");
                }
            }
            w.WriteEndArray(); w.WriteString("stop_reason", StopReason(finish)); w.WriteNull("stop_sequence");
            w.WritePropertyName("usage"); WriteUsage(w, Usage(root)); w.WriteEndObject();
        }), stopSequences);
    }

    internal static string Finish(JsonElement root)
    {
        Require(!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null, ErrorMessage(root, "Responses request failed."));
        if (Text(root, "status") == "incomplete")
        {
            var reason = root.TryGetProperty("incomplete_details", out var details) ? Text(details, "reason") : null;
            return reason switch { "max_output_tokens" => "length", "content_filter" => "content_filter", _ => throw new TranslationException("Responses request was incomplete.") };
        }
        Require(Text(root, "status") == "completed", "Responses request did not complete.");
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            if (output.EnumerateArray().Any(i => Text(i, "type") == "function_call")) return "tool_calls";
            if (output.EnumerateArray().Any(i => Text(i, "type") == "message" && i.TryGetProperty("content", out var parts)
                && parts.ValueKind == JsonValueKind.Array && parts.EnumerateArray().Any(p => Text(p, "type") == "refusal"))) return "content_filter";
        }
        return "stop";
    }

    internal static AnthropicTranslator.Usage Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return default;
        var cached = usage.TryGetProperty("input_tokens_details", out var details) ? Number(details, "cached_tokens") : 0;
        return new(Math.Max(0, Number(usage, "input_tokens") - cached), Number(usage, "output_tokens"), cached);
    }

    private static long Number(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var n)
        && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var count) ? Math.Max(0, count) : 0;
    private static void Copy(Utf8JsonWriter w, JsonElement root, string name, string? target = null)
    { if (root.TryGetProperty(name, out var value)) { w.WritePropertyName(target ?? name); value.WriteTo(w); } }
    internal static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var w = new Utf8JsonWriter(output)) write(w);
        return output.ToArray();
    }
}
