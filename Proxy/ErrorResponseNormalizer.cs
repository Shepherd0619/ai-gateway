using System.Text.Json;

namespace AiGateway.Proxy;

internal static class ErrorResponseNormalizer
{
    private const string GenericProviderMessage = "Provider returned error";

    internal static byte[] Normalize(byte[] responseBytes, bool isErrorResponse = true)
    {
        if (!isErrorResponse || responseBytes.Length == 0)
            return responseBytes;

        try
        {
            using var document = JsonDocument.Parse(responseBytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("raw", out var raw) ||
                raw.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(raw.GetString()) ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
                return responseBytes;

            using var rawDocument = JsonDocument.Parse(raw.GetString()!);
            var rawRoot = rawDocument.RootElement;
            if (rawRoot.ValueKind != JsonValueKind.Object ||
                !rawRoot.TryGetProperty("error", out var rawError) ||
                rawError.ValueKind != JsonValueKind.Object ||
                !rawError.TryGetProperty("message", out var providerMessage) ||
                providerMessage.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(providerMessage.GetString()))
                return responseBytes;

            if (error.TryGetProperty("message", out var currentMessage) &&
                currentMessage.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(currentMessage.GetString()) &&
                !string.Equals(currentMessage.GetString(), GenericProviderMessage, StringComparison.Ordinal))
                return responseBytes;

            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("error"))
                        WriteErrorWithMessage(writer, property.Value, providerMessage.GetString()!);
                    else
                        property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return output.ToArray();
        }
        catch (JsonException)
        {
            return responseBytes;
        }
    }

    private static void WriteErrorWithMessage(Utf8JsonWriter writer, JsonElement error, string message)
    {
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        foreach (var property in error.EnumerateObject())
        {
            if (property.NameEquals("message"))
                writer.WriteString("message", message);
            else
                property.WriteTo(writer);
        }
        if (!error.TryGetProperty("message", out _))
            writer.WriteString("message", message);
        writer.WriteEndObject();
    }
}
