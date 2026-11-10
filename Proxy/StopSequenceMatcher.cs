using System.Text.Json;

namespace AiGateway.Proxy;

// Responses has no native stop parameter. Retain only a possible delimiter
// prefix between text deltas so delimiters never leak across SSE boundaries.
internal sealed class StopSequenceMatcher(IReadOnlyList<string>? sequences)
{
    private readonly IReadOnlyList<string> _sequences = sequences ?? [];
    private string _pending = "";
    internal string? Matched { get; private set; }

    internal string Append(string text) => Process(text, final: false);

    private string Process(string text, bool final)
    {
        if (Matched is not null) return "";
        if (_sequences.Count == 0) return text;
        var value = _pending + text;
        _pending = "";
        for (var index = 0; index < value.Length; index++)
        {
            var tail = value.AsSpan(index);
            foreach (var sequence in _sequences)
                if (tail.StartsWith(sequence, StringComparison.Ordinal))
                {
                    Matched = sequence;
                    return value[..index];
                }
            if (!final && _sequences.Any(sequence => sequence.Length > value.Length - index
                && sequence.AsSpan().StartsWith(value.AsSpan(index), StringComparison.Ordinal)))
            {
                _pending = value[index..];
                return value[..index];
            }
        }
        return value;
    }

    internal string Flush() => Process("", final: true);

    internal static byte[] Apply(byte[] body, IReadOnlyList<string>? sequences)
    {
        if (sequences is null || sequences.Count == 0) return body;
        var matcher = new StopSequenceMatcher(sequences);
        using var doc = JsonDocument.Parse(body);
        return AnthropicResponsesTranslator.Json(w =>
        {
            w.WriteStartObject(); w.WritePropertyName("content"); w.WriteStartArray();
            foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
            {
                if (matcher.Matched is not null) break;
                if (AnthropicTranslator.Text(block, "type") == "text")
                    WriteText(w, matcher.Append(AnthropicTranslator.Text(block, "text") ?? ""));
                else
                {
                    WriteText(w, matcher.Flush());
                    if (matcher.Matched is not null) break;
                    block.WriteTo(w);
                }
            }
            WriteText(w, matcher.Flush()); w.WriteEndArray();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "content") continue;
                if (matcher.Matched is { } stop && property.Name is "stop_reason" or "stop_sequence")
                    w.WriteString(property.Name, property.Name == "stop_reason" ? "stop_sequence" : stop);
                else property.WriteTo(w);
            }
            w.WriteEndObject();
        });
    }

    private static void WriteText(Utf8JsonWriter w, string text)
    {
        if (text.Length == 0) return;
        w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", text); w.WriteEndObject();
    }
}
