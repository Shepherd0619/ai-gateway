using System.Text;
using System.Text.Json;
using AiGateway.Configuration;

namespace AiGateway.Proxy;

// Only used for backends that explicitly opt into Anthropic -> Chat Completions translation.
// Request bodies stay in UTF-8; untouched passthrough routes retain their existing behavior.
internal static class AnthropicTranslator
{
    internal static byte[] TranslateRequest(byte[] body, ReasoningFormat reasoningFormat = ReasoningFormat.OpenAI)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Require(root.ValueKind == JsonValueKind.Object, "Request must be a JSON object.");
        Require(!string.IsNullOrWhiteSpace(Text(root, "model")), "model is required.");
        Require(root.TryGetProperty("max_tokens", out var max) && max.TryGetInt64(out var count) && count > 0, "max_tokens must be a positive integer.");
        Require(root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0, "messages must be a non-empty array.");
        var selectedTools = DeferredToolCatalog.Select(root, messages, reasoningFormat);
        using var output = new MemoryStream();
        using (var w = new Utf8JsonWriter(output))
        {
            w.WriteStartObject();
            Copy(w, root, "model");
            Copy(w, root, "max_tokens", reasoningFormat == ReasoningFormat.OpenAI ? "max_completion_tokens" : "max_tokens");
            foreach (var field in new[] { "temperature", "top_p", "stream" }) Copy(w, root, field);
            Copy(w, root, "stop_sequences", "stop");
            WriteReasoning(w, root, reasoningFormat);
            if (root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            {
                w.WritePropertyName("stream_options");
                w.WriteStartObject(); w.WriteBoolean("include_usage", true); w.WriteEndObject();
            }
            w.WritePropertyName("messages"); w.WriteStartArray();
            if (root.TryGetProperty("system", out var system))
                WriteSystemMessage(w, system);
            var userRun = new List<JsonElement>();
            var messageIndex = 0;
            foreach (var message in messages.EnumerateArray())
            {
                var index = messageIndex++;
                if (Text(message, "role") == "user") { userRun.Add(message); continue; }
                if (userRun.Count > 0) { WriteUserMessages(w, userRun); userRun.Clear(); }
                if (Text(message, "role") == "system")
                {
                    Require(message.TryGetProperty("content", out var content), $"messages[{index}].content is required.");
                    WriteSystemMessage(w, content);
                    continue;
                }
                WriteMessage(w, message, index);
            }
            if (userRun.Count > 0) WriteUserMessages(w, userRun);
            w.WriteEndArray();
            if (selectedTools is not null)
            {
                w.WritePropertyName("tools"); w.WriteStartArray();
                foreach (var tool in selectedTools)
                {
                    w.WriteStartObject(); w.WriteString("type", "function"); w.WritePropertyName("function"); w.WriteStartObject();
                    Copy(w, tool, "name"); Copy(w, tool, "description"); Copy(w, tool, "input_schema", "parameters");
                    w.WriteEndObject(); w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (root.TryGetProperty("tool_choice", out var choice))
            {
                w.WritePropertyName("tool_choice");
                switch (Text(choice, "type"))
                {
                    case "auto": w.WriteStringValue("auto"); break;
                    case "any": w.WriteStringValue("required"); break;
                    case "none": w.WriteStringValue("none"); break;
                    case "tool":
                        Require(!string.IsNullOrWhiteSpace(Text(choice, "name")), "tool_choice.name is required.");
                        w.WriteStartObject(); w.WriteString("type", "function"); w.WritePropertyName("function");
                        w.WriteStartObject(); Copy(w, choice, "name"); w.WriteEndObject(); w.WriteEndObject(); break;
                    default: throw new TranslationException("Unsupported tool_choice type.");
                }
                if (choice.TryGetProperty("disable_parallel_tool_use", out var disabled))
                {
                    Require(disabled.ValueKind is JsonValueKind.True or JsonValueKind.False, "disable_parallel_tool_use must be boolean.");
                    w.WriteBoolean("parallel_tool_calls", !disabled.GetBoolean());
                }
            }
            w.WriteEndObject();
        }
        return output.ToArray();
    }

    private static void WriteReasoning(Utf8JsonWriter w, JsonElement root, ReasoningFormat format)
    {
        string? effort = null;
        long? budget = null;
        if (root.TryGetProperty("output_config", out var config))
        {
            Require(config.ValueKind == JsonValueKind.Object, "output_config must be an object.");
            if (config.TryGetProperty("effort", out _))
            {
                effort = Text(config, "effort");
                Require(effort is "low" or "medium" or "high" or "xhigh" or "max", "Unsupported output_config.effort.");
            }
        }

        if (root.TryGetProperty("thinking", out var thinking))
        {
            Require(thinking.ValueKind == JsonValueKind.Object, "thinking must be an object.");
            switch (Text(thinking, "type"))
            {
                case "disabled": effort = "none"; break;
                case "adaptive": effort ??= "high"; break;
                case "enabled":
                    Require(thinking.TryGetProperty("budget_tokens", out var value)
                        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var tokens) && tokens > 0,
                        "thinking.budget_tokens must be a positive integer when thinking is enabled.");
                    budget = thinking.GetProperty("budget_tokens").GetInt64();
                    break;
                default: throw new TranslationException("Unsupported thinking.type.");
            }
        }
        if (effort is null && budget is null) return;
        if (format == ReasoningFormat.OpenRouter)
        {
            w.WritePropertyName("reasoning"); w.WriteStartObject();
            w.WriteBoolean("enabled", effort != "none");
            // OpenRouter accepts either an effort or a numeric budget, not both.
            if (effort is not null && effort != "none") w.WriteString("effort", effort);
            else if (effort is null && budget is not null) w.WriteNumber("max_tokens", budget.Value);
            w.WriteEndObject();
        }
        else
        {
            // Standard Chat Completions has no numeric reasoning budget. These
            // are approximate gateway policy thresholds, not equivalent token limits.
            effort ??= budget <= 1024 ? "low" : budget <= 8192 ? "medium" : "high";
            w.WriteString("reasoning_effort", effort);
        }
    }

    private static void WriteSystemMessage(Utf8JsonWriter w, JsonElement content)
    {
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : SystemText(content);
        if (text.Length > 0) WriteMessage(w, "system", text);
    }

    private static string SystemText(JsonElement system)
    {
        Require(system.ValueKind == JsonValueKind.Array, "system must be text or an array of text blocks.");
        var texts = new List<string>();
        foreach (var block in system.EnumerateArray())
        {
            Require(Text(block, "type") == "text" && Text(block, "text") is not null, "Unsupported system block.");
            var text = Text(block, "text")!;
            if (!text.StartsWith("x-anthropic-billing-header:", StringComparison.Ordinal)) texts.Add(text);
        }
        return string.Join("\n", texts);
    }

    private static void WriteMessage(Utf8JsonWriter w, string role, string content)
    {
        w.WriteStartObject(); w.WriteString("role", role); w.WriteString("content", content); w.WriteEndObject();
    }

    private static void WriteMessage(Utf8JsonWriter w, JsonElement message, int index)
    {
        var role = Text(message, "role");
        if (role != "assistant")
        {
            var received = "missing";
            if (message.ValueKind != JsonValueKind.Object) received = $"message is {message.ValueKind}";
            else if (message.TryGetProperty("role", out var value))
            {
                // Only role metadata enters diagnostics, never message content.
                // Bound and JSON-escape strings to keep arbitrary input out of log lines.
                if (value.ValueKind == JsonValueKind.String)
                {
                    var roleText = value.GetString()!;
                    received = JsonSerializer.Serialize(roleText.Length > 64 ? roleText[..64] + "..." : roleText);
                }
                else received = value.ValueKind.ToString();
            }
            throw new TranslationException($"messages[{index}].role must be user, assistant or system; received {received}.");
        }
        Require(message.TryGetProperty("content", out var content), "Message content is required.");
        if (content.ValueKind == JsonValueKind.String) { WriteMessage(w, role!, content.GetString()!); return; }
        Require(content.ValueKind == JsonValueKind.Array, "Message content must be text or an array.");
        var blocks = new List<JsonElement>();
        var calls = new List<JsonElement>();
        var reasoning = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            switch (Text(block, "type"))
            {
                case "tool_use":
                    Require(role == "assistant", "tool_use is only valid in assistant messages.");
                    Require(!string.IsNullOrWhiteSpace(Text(block, "id")) && !string.IsNullOrWhiteSpace(Text(block, "name")), "tool_use id and name are required.");
                    Require(block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object, "tool_use.input must be an object.");
                    calls.Add(block); break;
                case "thinking":
                    Require(role == "assistant", "thinking is only valid in assistant messages.");
                    reasoning.Add(Text(block, "thinking") ?? ""); break;
                case "redacted_thinking": break; // Opaque Anthropic signatures have no OpenAI equivalent.
                case "text": case "image": blocks.Add(block); break;
                default: throw new TranslationException($"Unsupported Anthropic content block '{Text(block, "type")}'.");
            }
        }
        w.WriteStartObject(); w.WriteString("role", role);
        w.WritePropertyName("content");
        if (blocks.Count == 0) w.WriteNullValue(); else WriteContent(w, blocks);
        if (reasoning.Count > 0) w.WriteString("reasoning_content", string.Join("\n", reasoning));
        if (calls.Count > 0)
        {
            w.WritePropertyName("tool_calls"); w.WriteStartArray();
            foreach (var call in calls)
            {
                w.WriteStartObject(); w.WriteString("id", Text(call, "id")); w.WriteString("type", "function");
                w.WritePropertyName("function"); w.WriteStartObject(); Copy(w, call, "name");
                w.WriteString("arguments", call.GetProperty("input").GetRawText()); w.WriteEndObject(); w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteUserMessages(Utf8JsonWriter w, List<JsonElement> messages)
    {
        // Anthropic combines consecutive user turns. Keep every parallel tool
        // reply in that run adjacent before attaching images or additional text.
        var blocks = new List<JsonElement>();
        var hasToolResults = false;
        foreach (var message in messages)
        {
            Require(message.TryGetProperty("content", out var content), "Message content is required.");
            if (content.ValueKind == JsonValueKind.String) { blocks.Add(content); continue; }
            Require(content.ValueKind == JsonValueKind.Array, "Message content must be text or an array.");
            foreach (var block in content.EnumerateArray())
            {
                switch (Text(block, "type"))
                {
                    case "tool_result": WriteToolResult(w, block, blocks); hasToolResults = true; break;
                    case "text": case "image": blocks.Add(block); break;
                    default: throw new TranslationException($"Unsupported user content block '{Text(block, "type")}'.");
                }
            }
        }
        if (blocks.Count > 0 || !hasToolResults) WriteContentMessage(w, "user", blocks);
    }

    private static void WriteToolResult(Utf8JsonWriter w, JsonElement block, List<JsonElement> userBlocks)
    {
        var id = Text(block, "tool_use_id");
        Require(!string.IsNullOrWhiteSpace(id), "tool_result.tool_use_id is required.");
        var texts = new List<string>();
        if (block.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) texts.Add(content.GetString()!);
            else
            {
                Require(content.ValueKind == JsonValueKind.Array, "tool_result.content must be text or an array.");
                var hasImages = false;
                foreach (var part in content.EnumerateArray())
                {
                    switch (Text(part, "type"))
                    {
                        case "text":
                            Require(Text(part, "text") is not null, "Text block requires text.");
                            texts.Add(Text(part, "text")!); break;
                        case "tool_reference":
                            texts.Add($"Tool available: {DeferredToolCatalog.ReferenceName(part)}"); break;
                        case "image":
                            if (!hasImages)
                                userBlocks.Add(JsonSerializer.SerializeToElement(new { type = "text", text = $"Image result from tool call {id}:" }));
                            hasImages = true; userBlocks.Add(part); break;
                        default: throw new TranslationException($"Unsupported tool result content '{Text(part, "type")}'.");
                    }
                }
                if (hasImages) texts.Add("[Image result attached in the following user message.]");
            }
        }
        var result = string.Join("\n", texts);
        if (block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
            result = "[Tool error]\n" + result;
        // Chat Completions tool messages support text only. Images are moved to
        // a user message after all parallel tool replies, retaining their call ID.
        w.WriteStartObject(); w.WriteString("role", "tool"); w.WriteString("tool_call_id", id);
        w.WriteString("content", result); w.WriteEndObject();
    }

    private static void WriteContentMessage(Utf8JsonWriter w, string role, List<JsonElement> blocks)
    {
        w.WriteStartObject(); w.WriteString("role", role); w.WritePropertyName("content"); WriteContent(w, blocks); w.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter w, List<JsonElement> blocks)
    {
        if (blocks.All(b => b.ValueKind == JsonValueKind.String || Text(b, "type") == "text"))
        {
            foreach (var b in blocks) Require(b.ValueKind == JsonValueKind.String || Text(b, "text") is not null, "Text block requires text.");
            w.WriteStringValue(string.Join("\n", blocks.Select(b => b.ValueKind == JsonValueKind.String ? b.GetString() : Text(b, "text")))); return;
        }
        w.WriteStartArray();
        foreach (var block in blocks)
        {
            w.WriteStartObject();
            if (block.ValueKind == JsonValueKind.String)
            {
                w.WriteString("type", "text"); w.WriteString("text", block.GetString()); w.WriteEndObject(); continue;
            }
            switch (Text(block, "type"))
            {
                case "text":
                    Require(Text(block, "text") is not null, "Text block requires text.");
                    w.WriteString("type", "text"); Copy(w, block, "text"); break;
                case "image":
                    Require(block.TryGetProperty("source", out var source), "Image source is required.");
                    var url = Text(source, "type") switch
                    {
                        "url" => Text(source, "url"),
                        "base64" when Text(source, "media_type") is { } mime && Text(source, "data") is { } data => $"data:{mime};base64,{data}",
                        _ => null
                    };
                    Require(!string.IsNullOrWhiteSpace(url), "Unsupported image source.");
                    w.WriteString("type", "image_url"); w.WritePropertyName("image_url"); w.WriteStartObject(); w.WriteString("url", url); w.WriteEndObject(); break;
                default: throw new TranslationException($"Unsupported tool result content '{Text(block, "type")}'.");
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    internal static byte[] TranslateResponse(byte[] body, string originalModel)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Require(!root.TryGetProperty("error", out _), ErrorMessage(root, "Upstream returned an error."));
        Require(root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0, "Upstream returned no completion choices.");
        var choice = choices[0];
        Require(choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object, "Upstream returned no assistant message.");
        using var output = new MemoryStream();
        using (var w = new Utf8JsonWriter(output))
        {
            w.WriteStartObject();
            WriteMessageHeader(w, Text(root, "id"), originalModel);
            w.WritePropertyName("content"); w.WriteStartArray();
            var reasoning = Text(message, "reasoning_content") ?? Text(message, "reasoning");
            if (!string.IsNullOrEmpty(reasoning)) WriteTextBlock(w, "thinking", reasoning);
            var text = Text(message, "content");
            if (!string.IsNullOrEmpty(text)) WriteTextBlock(w, "text", text);
            if (Text(message, "refusal") is { Length: > 0 } refusal) WriteTextBlock(w, "text", refusal);
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    Require(call.TryGetProperty("function", out var function) && !string.IsNullOrWhiteSpace(Text(function, "name")) && !string.IsNullOrWhiteSpace(Text(call, "id")), "Invalid upstream tool call.");
                    using var args = JsonDocument.Parse(Text(function, "arguments") is { Length: > 0 } arguments ? arguments : "{}");
                    Require(args.RootElement.ValueKind == JsonValueKind.Object, "Tool arguments must be a JSON object.");
                    w.WriteStartObject(); w.WriteString("type", "tool_use"); Copy(w, call, "id"); Copy(w, function, "name");
                    w.WritePropertyName("input"); args.RootElement.WriteTo(w); w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteString("stop_reason", StopReason(Text(choice, "finish_reason")));
            w.WriteNull("stop_sequence");
            w.WritePropertyName("usage"); WriteUsage(w, ReadUsage(root));
            w.WriteEndObject();
        }
        return output.ToArray();
    }

    internal static void WriteMessageHeader(Utf8JsonWriter w, string? id, string model)
    {
        w.WriteString("id", id is not null ? "msg_" + id : "msg_" + Guid.NewGuid().ToString("N"));
        w.WriteString("type", "message"); w.WriteString("role", "assistant"); w.WriteString("model", model);
    }

    private static void WriteTextBlock(Utf8JsonWriter w, string type, string value)
    {
        w.WriteStartObject(); w.WriteString("type", type); w.WriteString(type, value);
        if (type == "thinking") w.WriteString("signature", "");
        w.WriteEndObject();
    }

    internal static string StopReason(string? finish) => finish switch
    {
        "tool_calls" or "function_call" => "tool_use",
        "length" => "max_tokens",
        "content_filter" => "refusal",
        "stop" => "end_turn",
        _ => throw new TranslationException("Upstream returned no supported finish_reason.")
    };

    internal readonly record struct Usage(long Input, long Output, long Cached);
    internal static Usage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return default;
        var cached = usage.TryGetProperty("prompt_tokens_details", out var details) ? Number(details, "cached_tokens") : 0;
        return new Usage(Math.Max(0, Number(usage, "prompt_tokens") - cached), Number(usage, "completion_tokens"), cached);
    }

    private static long Number(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
    internal static void WriteUsage(Utf8JsonWriter w, Usage usage)
    {
        w.WriteStartObject(); w.WriteNumber("input_tokens", usage.Input); w.WriteNumber("output_tokens", usage.Output);
        w.WriteNumber("cache_creation_input_tokens", 0); w.WriteNumber("cache_read_input_tokens", usage.Cached); w.WriteEndObject();
    }

    internal static string ErrorMessage(JsonElement root, string fallback)
    {
        if (root.ValueKind != JsonValueKind.Object) return fallback;
        if (root.TryGetProperty("error", out var error)) return Text(error, "message") ?? (error.ValueKind == JsonValueKind.String ? error.GetString()! : fallback);
        return Text(root, "message") ?? fallback;
    }

    internal static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static void Require(bool condition, string message) { if (!condition) throw new TranslationException(message); }
    private static void Copy(Utf8JsonWriter w, JsonElement root, string name, string? target = null)
    {
        if (!root.TryGetProperty(name, out var value)) return;
        w.WritePropertyName(target ?? name); value.WriteTo(w);
    }
}

internal sealed class TranslationException(string message) : Exception(message);
