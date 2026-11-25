using System.Net;
using System.Text;
using AiGateway.Authentication;
using AiGateway.Compliance;
using AiGateway.Configuration;
using AiGateway.Discovery;
using AiGateway.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class ServerKeyRoutingTests
{
    [Theory]
    [InlineData("ChatCompletions","/v1/chat/completions")]
    [InlineData("Responses","/v1/responses")]
    public async Task TranslatedRoutesUseProviderCredential(string protocol,string path)
    {
        await using var f=await Fixture.CreateAsync(translate:true,protocol:protocol);
        var key=await f.Db.Store.IssueAsync("translated",null);
        f.Upstream.Response=protocol=="Responses"
            ? """{"id":"r","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello"}]}],"usage":{"input_tokens":1,"output_tokens":1}}"""
            : """{"id":"c","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
        var ctx=await f.Invoke(key.ApiKey,"""{"model":"claude","max_tokens":5,"messages":[{"role":"user","content":"hello"}]}""");
        Assert.Equal(200,ctx.Response.StatusCode);
        Assert.Equal("Bearer secondary-secret",f.Upstream.Authorization);
        Assert.Equal("https://secondary.example/api"+path,f.Upstream.Url);
        Assert.Contains("hello",Body(ctx));
    }

    [Fact]
    public async Task GatewayUsesFinalBackendKeyAndByokRemainsUnchanged()
    {
        await using var f = await Fixture.CreateAsync();
        var key = await f.Db.Store.IssueAsync("desktop", null);
        var ctx = await f.Invoke(key.ApiKey);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("Bearer secondary-secret", f.Upstream.Authorization);
        Assert.Equal("backend-secondary", f.Upstream.ClientName);
        f.Credentials.Save("secondary", "temporary-secret");
        await f.Invoke(key.ApiKey);
        Assert.Equal("Bearer temporary-secret", f.Upstream.Authorization);
        await f.Invoke("byok-key");
        Assert.Equal("Bearer byok-key", f.Upstream.Authorization);
        Assert.NotEqual("Bearer " + key.ApiKey, f.Upstream.Authorization);
    }

    [Fact]
    public async Task ClassifierUsesItsOwnBackendKeyAndProxy()
    {
        await using var f = await Fixture.CreateAsync();
        var key = await f.Db.Store.IssueAsync("desktop", null);
        await f.Invoke(key.ApiKey, """{"model":"claude","system":[{"type":"text","text":"x-anthropic-billing-header: test"},{"type":"text","text":"You are a security monitor checking tools"}],"messages":[],"max_tokens":5}""");
        Assert.Equal("Bearer default-secret", f.Upstream.Authorization);
        Assert.Equal("backend-openrouter-proxy-test-proxy", f.Upstream.ClientName);
    }

    [Fact]
    public async Task MissingProviderOrInvalidGatewayKeyNeverReachesUpstream()
    {
        await using var f = await Fixture.CreateAsync();
        var key = await f.Db.Store.IssueAsync("desktop", null);
        var ctx = await f.Invoke(key.ApiKey, """{"model":"missing","messages":[]}""");
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Contains("provider_key_not_configured", Body(ctx));
        Assert.Equal(0, f.Upstream.Calls);
        ctx = await f.Invoke("sk-aigw-invalid");
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Equal(0, f.Upstream.Calls);
        Assert.NotNull(Assert.Single(await f.Db.Store.ListAsync(50,0)).LastUsedAt);
    }

    [Theory]
    [InlineData(true, null, 401)]
    [InlineData(true, "byok", 200)]
    [InlineData(true, "sk-aigw-invalid", 401)]
    [InlineData(false, null, 200)]
    [InlineData(false, "sk-aigw-invalid", 401)]
    public async Task LocalDiscoveryUsesCredentialRules(bool enabled, string? token, int expected)
    {
        await using var f = await Fixture.CreateAsync(enabled);
        var ctx = f.Context(token, ""); ctx.Request.Path = "/v1/models"; ctx.Request.Method = "GET";
        await ModelDiscoveryEndpoints.HandleModels(ctx, f.Discovery, f.Proxy);
        Assert.Equal(expected, ctx.Response.StatusCode);
        Assert.Equal(0, f.Upstream.Calls);
    }

    [Fact]
    public async Task DiscoveryOffUsesDefaultBackendProviderKey()
    {
        await using var f = await Fixture.CreateAsync(discovery: false);
        var key = await f.Db.Store.IssueAsync("models", null);
        var ctx = f.Context(key.ApiKey, ""); ctx.Request.Path = "/v1/models"; ctx.Request.Method = "GET";
        await ModelDiscoveryEndpoints.HandleModels(ctx, f.Discovery, f.Proxy);
        Assert.Equal("Bearer default-secret", f.Upstream.Authorization);
        Assert.Equal("backend-openrouter", f.Upstream.ClientName);
    }

    [Fact]
    public async Task UpstreamErrorsAndStreamingStillRecordAuthentication()
    {
        await using var f = await Fixture.CreateAsync();
        var key = await f.Db.Store.IssueAsync("stream", null);
        f.Upstream.Status = 401;
        Assert.Equal(401, (await f.Invoke(key.ApiKey)).Response.StatusCode);
        Assert.NotNull(Assert.Single(await f.Db.Store.ListAsync(50,0)).LastUsedAt);
        f.Upstream.Status = 200; f.Upstream.Stream = true;
        var ctx = await f.Invoke(key.ApiKey, """{"model":"claude","stream":true,"messages":[]}""");
        Assert.Equal("text/event-stream", ctx.Response.ContentType);
        Assert.Contains("data:", Body(ctx));
        Assert.Equal("Bearer secondary-secret", f.Upstream.Authorization);
    }

    private static string Body(DefaultHttpContext ctx) => Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());

    internal sealed class Fixture : IAsyncDisposable
    {
        public KeyTestDatabase Db { get; }
        public RecordingUpstream Upstream { get; } = new();
        public BackendCredentialStore Credentials { get; }
        public RuntimeModelDiscoveryStore Discovery { get; }
        public ProxyHandler Proxy { get; }
        private readonly ServiceProvider _provider = new ServiceCollection().AddLogging().BuildServiceProvider();

        private Fixture(KeyTestDatabase db, bool enabled, bool discovery, bool translate, string protocol)
        {
            Db = db;
            var backends = Options.Create(new BackendOptions
            {
                ["openrouter"] = new() { BaseUrl = "https://default.example/api", ApiKey = "default-secret" },
                ["secondary"] = new() { BaseUrl = "https://secondary.example/api", ApiKey = "secondary-secret", TranslateAnthropicToOpenAI=translate,OpenAIProtocol=Enum.Parse<OpenAIProtocol>(protocol) },
                ["unkeyed"] = new() { BaseUrl = "https://unkeyed.example" }
            });
            var admin = Options.Create(new AdminOptions { RuntimeConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mappings.json") });
            var proxies = Options.Create(new ProxyServerOptions { ["test-proxy"] = new() { Address = "socks5://127.0.0.1:7890" } });
            var mapping = new RuntimeMappingStore(Options.Create(new ModelMappingOptions { Rules = [new() { Prefix = "claude", Backend = "secondary", Target = "remote" },new() { Prefix="missing",Backend="unkeyed" }] }), admin, proxies, backends, NullLogger<RuntimeMappingStore>.Instance);
            var classifier = new RuntimeClassifierStore(Options.Create(new ClassifierOptions { Target="classifier",Backend="openrouter",ProxyServer="test-proxy" }), admin, backends, proxies, NullLogger<RuntimeClassifierStore>.Instance);
            Credentials = new(backends);
            Proxy = new(Upstream, NullLogger<ProxyHandler>.Instance, new ModelMapper(mapping), new ComplianceLogWriter(NullLogger<ComplianceLogWriter>.Instance,Options.Create(new ComplianceLogOptions())),classifier,backends,
                authentication: ClientAuthenticationTests.Auth(enabled, db.Store), credentials: Credentials);
            Discovery = new(Options.Create(new ModelDiscoveryOptions { Enabled = discovery, Models = [new() { Id = "claude" }] }), admin, NullLogger<RuntimeModelDiscoveryStore>.Instance);
        }
        public static async Task<Fixture> CreateAsync(bool enabled=true,bool discovery=true,bool translate=false,string protocol="ChatCompletions") => new(await KeyTestDatabase.CreateAsync(),enabled,discovery,translate,protocol);
        public DefaultHttpContext Context(string? token, string body)
        {
            var ctx = ClientAuthenticationTests.Context(token);ctx.RequestServices=_provider;ctx.Request.Method="POST";ctx.Request.Path="/v1/messages";
            ctx.Request.ContentType="application/json";ctx.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(body));return ctx;
        }
        public async Task<DefaultHttpContext> Invoke(string? token, string body="""{"model":"claude","max_tokens":5,"messages":[]}""")
        { var ctx=Context(token,body);await Proxy.Invoke(ctx);return ctx; }
        public async ValueTask DisposeAsync() { Upstream.Dispose(); await _provider.DisposeAsync(); await Db.DisposeAsync(); }
    }

    internal sealed class RecordingUpstream : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls, Status=200;
        public bool Stream;
        public string? Authorization,ClientName,Url;
        public string Response="{\"model\":\"remote\"}";
        public HttpClient CreateClient(string name) { ClientName=name;return new(this,false); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Authorization=request.Headers.Authorization?.ToString();Url=request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status) { Content=new StringContent(Stream ? "data: {\"model\":\"remote\"}\n\n" : Response,Encoding.UTF8, Stream ? "text/event-stream" : "application/json") });
        }
    }
}
