namespace AiGateway.Configuration;

internal sealed record BackendConfig
{
    public string BaseUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ApiKey { get; init; }

    public bool TranslateAnthropicToOpenAI { get; init; }

    public ReasoningFormat ReasoningFormat { get; init; } = ReasoningFormat.OpenAI;

    public OpenAIProtocol OpenAIProtocol { get; init; } = OpenAIProtocol.ChatCompletions;

    public override string ToString() => $"BackendConfig {{ BaseUrl = {BaseUrl} }}";
}

internal enum ReasoningFormat { OpenAI, OpenRouter }
internal enum OpenAIProtocol { ChatCompletions, Responses }
