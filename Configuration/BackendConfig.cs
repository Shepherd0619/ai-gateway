namespace AiGateway.Configuration;

internal sealed record BackendConfig
{
    public string BaseUrl { get; init; } = "";

    public bool TranslateAnthropicToOpenAI { get; init; }

    public ReasoningFormat ReasoningFormat { get; init; } = ReasoningFormat.OpenAI;

    public OpenAIProtocol OpenAIProtocol { get; init; } = OpenAIProtocol.ChatCompletions;
}

internal enum ReasoningFormat { OpenAI, OpenRouter }
internal enum OpenAIProtocol { ChatCompletions, Responses }
