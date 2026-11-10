using System.Net;
using System.Text;
using System.Text.Json;
using AiGateway.Compliance;
using AiGateway.Configuration;
using AiGateway.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace AiGateway.Tests;

public sealed class TranslationRoutingTests
{
    private const string Request = """{"model":"claude-test","max_tokens":5,"messages":[{"role":"user","content":"hello"}]}""";
    private const string Completion = """{"id":"chatcmpl-test","choices":[{"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

    [Fact]
    public async Task ResponsesClassifier_WithStopSequences_ReturnsStoppedClassification()
    {
        using var f = new Fixture(classifier: true, protocol: "Responses", response: """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"allow</decision>extra"}]}]}""");
        var request = """{"model":"claude-test","max_tokens":100,"stop_sequences":["</decision>"],"system":[{"type":"text","text":"x-anthropic-billing-header: test"},{"type":"text","text":"You are a security monitor"}],"messages":[{"role":"user","content":"classify"}]}""";
        var ctx = await f.Invoke(request);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.EndsWith("/v1/responses", f.Url);
        using var sent = JsonDocument.Parse(f.Sent!);
        Assert.Equal("classifier-model", sent.RootElement.GetProperty("model").GetString());
        Assert.False(sent.RootElement.TryGetProperty("stop", out _));
        Assert.False(sent.RootElement.TryGetProperty("stop_sequences", out _));
        using var response = JsonDocument.Parse(Body(ctx));
        Assert.Equal("allow", response.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("stop_sequence", response.RootElement.GetProperty("stop_reason").GetString());
        Assert.Equal("</decision>", response.RootElement.GetProperty("stop_sequence").GetString());
    }

    [Fact]
    public async Task ResponsesStream_MatchesStopAcrossChunksAndKeepsUsage()
    {
        using var f = new Fixture(protocol: "Responses", response: "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"allow</dec\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"ision>hidden\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"allow</decision>hidden\"}]}],\"usage\":{\"input_tokens\":7,\"output_tokens\":9}}}\n\n");
        var ctx = await f.Invoke("""{"model":"claude-test","max_tokens":100,"stream":true,"stop_sequences":["</decision>"],"messages":[{"role":"user","content":"classify"}]}""");
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Contains("allow", Body(ctx));
        Assert.DoesNotContain("hidden", Body(ctx));
        var events = Body(ctx).Split('\n').Where(l => l.StartsWith("data: ")).Select(l => JsonDocument.Parse(l[6..]).RootElement.Clone()).ToArray();
        var final = events.Single(e => e.GetProperty("type").GetString() == "message_delta");
        Assert.Equal("stop_sequence", final.GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Equal("</decision>", final.GetProperty("delta").GetProperty("stop_sequence").GetString());
        Assert.Equal(9, final.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public async Task ResponsesBackend_RoutesMessagesToResponsesAndRestoresAnthropicEnvelope()
    {
        using var f = new Fixture(response: """{"id":"resp-a","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hello"}]}],"usage":{"input_tokens":3,"output_tokens":1}}""", protocol: "Responses");
        var ctx = await f.Invoke(Request, query: "?test=value");
        Assert.Equal("https://example.test/api/v1/responses?test=value", f.Url);
        using var sent = JsonDocument.Parse(f.Sent!);
        Assert.True(sent.RootElement.TryGetProperty("input", out _));
        Assert.False(sent.RootElement.TryGetProperty("messages", out _));
        using var response = JsonDocument.Parse(Body(ctx));
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("claude-test", response.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello", response.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ResponsesStreamingBackend_ConvertsEventsOnTheSelectedRoute()
    {
        using var f = new Fixture(response: "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"Hello\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello\"}]}]}}\n\n", protocol: "Responses");
        var ctx = await f.Invoke(Request.Replace("\"max_tokens\":5", "\"max_tokens\":5,\"stream\":true"));
        Assert.EndsWith("/v1/responses", f.Url);
        Assert.Equal("text/event-stream", ctx.Response.ContentType);
        Assert.Contains("event: message_start", Body(ctx));
        Assert.Contains("event: message_stop", Body(ctx));
        Assert.Contains("Hello", Body(ctx));
    }

    [Fact]
    public async Task ResponsesUpstreamError_PreservesOriginalMessage()
    {
        using var f = new Fixture(response: """{"error":{"message":"Model unavailable","type":"invalid_request_error"}}""", status: 400, protocol: "Responses");
        var ctx = await f.Invoke(Request);
        using var error = JsonDocument.Parse(Body(ctx));
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("Model unavailable", error.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("OpenAI", "max_completion_tokens", "max_tokens")]
    [InlineData("OpenRouter", "max_tokens", "max_completion_tokens")]
    public async Task TranslatedTokenLimit_UsesBackendDialect(string format, string field, string absentField)
    {
        using var f = new Fixture(reasoningFormat: format);
        await f.Invoke(Request);
        using var sent = JsonDocument.Parse(f.Sent!);
        Assert.Equal(5, sent.RootElement.GetProperty(field).GetInt32());
        Assert.False(sent.RootElement.TryGetProperty(absentField, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedParameterError_ReachesClientWithOriginalMessage(bool stream)
    {
        const string message = "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.";
        using var f = new Fixture(response: JsonSerializer.Serialize(new { error = new { message, type = "invalid_request_error", param = "max_tokens", code = "unsupported_parameter" } }), status: 400);
        var ctx = await f.Invoke(stream ? Request.Replace("\"max_tokens\":5", "\"max_tokens\":5,\"stream\":true") : Request);
        using var error = JsonDocument.Parse(Body(ctx));
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("application/json", ctx.Response.ContentType);
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", error.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(message, error.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(ctx.TraceIdentifier, ctx.Response.Headers["request-id"].ToString());
    }

    [Theory]
    [InlineData("{\"type\":\"enabled\",\"budget_tokens\":4096}", "", "{\"enabled\":true,\"max_tokens\":4096}")]
    [InlineData("{\"type\":\"adaptive\"}", "", "{\"enabled\":true,\"effort\":\"high\"}")]
    [InlineData("{\"type\":\"adaptive\"}", ",\"output_config\":{\"effort\":\"max\"}", "{\"enabled\":true,\"effort\":\"max\"}")]
    [InlineData("{\"type\":\"enabled\",\"budget_tokens\":4096}", ",\"output_config\":{\"effort\":\"low\"}", "{\"enabled\":true,\"effort\":\"low\"}")]
    [InlineData("{\"type\":\"disabled\"}", ",\"output_config\":{\"effort\":\"high\"}", "{\"enabled\":false}")]
    public async Task OpenRouterReasoning_UsesConfiguredDialect(string thinking, string extra, string expected)
    {
        using var f = new Fixture(reasoningFormat: "OpenRouter");
        await f.Invoke("{\"model\":\"claude-test\",\"max_tokens\":32000,\"thinking\":" + thinking + extra + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        using var sent = JsonDocument.Parse(f.Sent!);
        var reasoning = sent.RootElement.GetProperty("reasoning");
        using var wanted = JsonDocument.Parse(expected);
        Assert.Equal(wanted.RootElement.EnumerateObject().Count(), reasoning.EnumerateObject().Count());
        foreach (var property in wanted.RootElement.EnumerateObject())
            Assert.Equal(property.Value.GetRawText(), reasoning.GetProperty(property.Name).GetRawText());
        Assert.False(sent.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(sent.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task OpenAiReasoning_UsesStandardDialectRegardlessOfBackendName()
    {
        using var f = new Fixture(); // The backend is named openrouter, but configured for OpenAI format.
        await f.Invoke("""{"model":"claude-test","max_tokens":32000,"thinking":{"type":"enabled","budget_tokens":4096},"messages":[{"role":"user","content":"hi"}]}""");
        using var sent = JsonDocument.Parse(f.Sent!);
        Assert.Equal("medium", sent.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(sent.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task EnabledTranslation_RoutesToChatCompletionsAndReturnsAnthropicMessage()
    {
        using var f = new Fixture();
        var ctx = await f.Invoke(Request);
        Assert.Equal("https://example.test/api/v1/chat/completions", f.Url);
        Assert.Equal("backend-openrouter", f.ClientName);
        Assert.False(f.AnthropicVersion);
        Assert.Equal("Bearer test-key", f.Authorization);
        using var sent = JsonDocument.Parse(f.Sent!);
        Assert.Equal("remote-model", sent.RootElement.GetProperty("model").GetString());
        using var response = JsonDocument.Parse(Body(ctx));
        Assert.Equal("message", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("claude-test", response.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello", response.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task MissingOrDisabledFlag_PreservesAnthropicRoute(bool? enabled)
    {
        using var f = new Fixture(enabled, response: """{"model":"remote-model","type":"message","content":[{"type":"text","text":"ok"}]}""");
        var ctx = await f.Invoke(Request);
        Assert.EndsWith("/v1/messages", f.Url);
        Assert.True(f.AnthropicVersion);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("message", JsonDocument.Parse(Body(ctx)).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Classifier_UsesFinalBackendFlagAndStripsBillingBlock()
    {
        using var f = new Fixture(classifier: true);
        await f.Invoke("""{"model":"claude-test","max_tokens":5,"system":[{"type":"text","text":"x-anthropic-billing-header: abc"},{"type":"text","text":"You are a security monitor. Check tools."}],"messages":[{"role":"user","content":"safe?"}]}""");
        Assert.EndsWith("/v1/chat/completions", f.Url);
        Assert.Equal("backend-openrouter-proxy-test-proxy", f.ClientName);
        using var sent = JsonDocument.Parse(f.Sent!);
        Assert.Equal("classifier-model", sent.RootElement.GetProperty("model").GetString());
        Assert.DoesNotContain("x-anthropic-billing-header", f.Sent);
        Assert.Contains("You are a security monitor", f.Sent);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"model\":\"claude-test\",\"max_tokens\":\"wrong\",\"messages\":[]}")]
    [InlineData("{\"model\":\"claude-test\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"document\"}]}]}")]
    [InlineData("{\"model\":\"claude-test\",\"max_tokens\":32000,\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":\"4096\"},\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}")]
    public async Task InvalidRequest_ReturnsAnthropic400WithoutUpstreamCall(string request)
    {
        using var f = new Fixture();
        var ctx = await f.Invoke(request);
        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal(0, f.Calls);
        using var error = JsonDocument.Parse(Body(ctx));
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", error.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(401, "authentication_error")]
    [InlineData(429, "rate_limit_error")]
    [InlineData(503, "api_error")]
    public async Task UpstreamErrors_PreserveStatusAndTranslateEnvelope(int status, string type)
    {
        using var f = new Fixture(response: """{"error":{"message":"Unavailable","code":429}}""", status: status);
        var ctx = await f.Invoke(Request);
        Assert.Equal(status, ctx.Response.StatusCode);
        using var error = JsonDocument.Parse(Body(ctx));
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal(type, error.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("Unavailable", error.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"x\",\"function\":{\"name\":\"f\",\"arguments\":\"{broken\"}}]},\"finish_reason\":\"tool_calls\"}]}")]
    public async Task InvalidUpstreamResponse_ReturnsAnthropic502(string response)
    {
        using var f = new Fixture(response: response);
        var ctx = await f.Invoke(Request);
        Assert.Equal(502, ctx.Response.StatusCode);
        Assert.Equal("error", JsonDocument.Parse(Body(ctx)).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CountTokens_IsExplicitlyUnsupportedOnTranslationBackends()
    {
        using var f = new Fixture();
        var ctx = await f.Invoke(Request, "/v1/messages/count_tokens");
        Assert.Equal(501, ctx.Response.StatusCode);
        Assert.Equal(0, f.Calls);
    }

    [Fact]
    public async Task StreamingRequest_ConvertsSseAndPreservesQuery()
    {
        using var f = new Fixture(response: "data: {\"id\":\"s\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        var ctx = await f.Invoke(Request.Replace("\"max_tokens\":5", "\"max_tokens\":5,\"stream\":true"), query: "?test=value");
        Assert.Equal("https://example.test/api/v1/chat/completions?test=value", f.Url);
        Assert.Equal("text/event-stream", ctx.Response.ContentType);
        Assert.Contains("event: message_start", Body(ctx));
        Assert.Contains("event: message_stop", Body(ctx));
        Assert.DoesNotContain("[DONE]", Body(ctx));
    }

    private static string Body(DefaultHttpContext ctx) => Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory
    {
        private readonly HttpClient _client;
        private readonly ProxyHandler _handler;
        private readonly string _response;
        private readonly int _status;
        public string? Url, Sent, Authorization, ClientName;
        public bool AnthropicVersion;
        public int Calls;
        public Fixture(bool? enabled = true, string response = Completion, int status = 200, bool classifier = false, string reasoningFormat = "OpenAI", string protocol = "ChatCompletions")
        {
            _response = response; _status = status; _client = new HttpClient(this, false);
            var values = new Dictionary<string,string?> { ["openrouter:BaseUrl"] = "https://example.test/api" };
            values["openrouter:ReasoningFormat"] = reasoningFormat;
            values["openrouter:OpenAIProtocol"] = protocol;
            if (enabled is not null) values["openrouter:TranslateAnthropicToOpenAI"] = enabled.Value.ToString();
            var backends = Options.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build().Get<BackendOptions>()!);
            var admin = Options.Create(new AdminOptions { RuntimeConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "mappings.json") });
            var proxies = Options.Create(new ProxyServerOptions { ["test-proxy"] = new ProxyServerConfig { Address = "socks5://127.0.0.1:7890" } });
            var store = new RuntimeMappingStore(Options.Create(new ModelMappingOptions { Rules = [new MappingRule { Prefix = "claude", Target = "remote-model" }] }), admin, proxies, backends, NullLogger<RuntimeMappingStore>.Instance);
            var classifierStore = new RuntimeClassifierStore(Options.Create(new ClassifierOptions { Target = classifier ? "classifier-model" : "", Backend = "openrouter", ProxyServer = classifier ? "test-proxy" : null }), admin, backends, proxies, NullLogger<RuntimeClassifierStore>.Instance);
            var compliance = new ComplianceLogWriter(NullLogger<ComplianceLogWriter>.Instance, Options.Create(new ComplianceLogOptions()));
            _handler = new ProxyHandler(this, NullLogger<ProxyHandler>.Instance, new ModelMapper(store), compliance, classifierStore, backends);
        }
        public async Task<DefaultHttpContext> Invoke(string body, string path = "/v1/messages", string query = "")
        {
            var ctx = new DefaultHttpContext(); ctx.Request.Method = "POST"; ctx.Request.Path = path;
            ctx.Request.QueryString = new QueryString(query); ctx.Request.Headers["x-api-key"] = "test-key";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body)); ctx.Response.Body = new MemoryStream();
            await _handler.Invoke(ctx); return ctx;
        }
        public HttpClient CreateClient(string name) { ClientName = name; return _client; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; _ = cancellationToken;
            Url = request.RequestUri!.ToString(); Sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString(); AnthropicVersion = request.Headers.Contains("anthropic-version");
            return new HttpResponseMessage((HttpStatusCode)_status) { Content = new StringContent(_response, Encoding.UTF8, _response.StartsWith("data:") ? "text/event-stream" : "application/json") };
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }
}
