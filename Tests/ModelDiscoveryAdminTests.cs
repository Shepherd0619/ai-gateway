using AiGateway.Configuration;
using AiGateway.Compliance;
using AiGateway.Discovery;
using AiGateway.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class ModelDiscoveryAdminTests
{
    [Fact]
    public async Task EnabledDiscovery_ReturnsOpenAiByDefaultAndAnthropicWhenHeaderPresent()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, new ModelDiscoveryOptions
            {
                Enabled = true,
                Models = [new ModelDiscoveryModel { Id = "gpt-test", DisplayName = "GPT Test" }],
            });
            var context = new DefaultHttpContext();
            context.Request.Headers["x-api-key"] = "client-key";
            context.Response.Body = new MemoryStream();
            await ModelDiscoveryEndpoints.HandleModels(context, store, CreateProxyHandler());
            context.Response.Body.Position = 0;
            var openAi = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Contains("\"object\":\"list\"", openAi);
            Assert.Contains("\"owned_by\":\"ai-gateway\"", openAi);

            context = new DefaultHttpContext();
            context.Request.Headers["x-api-key"] = "client-key";
            context.Request.Headers["anthropic-version"] = "2023-06-01";
            context.Response.Body = new MemoryStream();
            await ModelDiscoveryEndpoints.HandleModels(context, store, CreateProxyHandler());
            context.Response.Body.Position = 0;
            var anthropic = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Contains("\"display_name\":\"GPT Test\"", anthropic);
            Assert.Contains("\"has_more\":false", anthropic);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task DisabledDiscovery_DelegatesToProxyWithoutChangingAuthentication()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, new ModelDiscoveryOptions { Models = [new ModelDiscoveryModel { Id = "hidden" }] });
            var proxy = CreateProxyHandler();
            var context = new DefaultHttpContext();
            context.Request.Path = "/v1/models";
            await ModelDiscoveryEndpoints.HandleModels(context, store, proxy);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);

            store.Save(new ModelDiscoveryOptions { Enabled = true, Models = [new ModelDiscoveryModel { Id = "private" }] });
            context = new DefaultHttpContext();
            context.Request.Path = "/v1/models";
            await ModelDiscoveryEndpoints.HandleModels(context, store, proxy);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);

            context = new DefaultHttpContext();
            context.Request.Path = "/v1/models";
            context.Request.Headers["Authorization"] = "Bearer key";
            context.Response.Body = new MemoryStream();
            await ModelDiscoveryEndpoints.HandleModels(context, store, proxy);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.True(context.Response.Body.Length > 0);
        }
        finally { Cleanup(path); }
    }

    private static RuntimeModelDiscoveryStore CreateStore(string path, ModelDiscoveryOptions options) =>

        new(Options.Create(options), Options.Create(new AdminOptions { RuntimeConfigPath = path }), NullLogger<RuntimeModelDiscoveryStore>.Instance);

    private static ProxyHandler CreateProxyHandler()
    {
        var backends = new BackendOptions { [BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = "https://backend.example/api" } };
        var mappingStore = new RuntimeMappingStore(
            Options.Create(new ModelMappingOptions()),
            Options.Create(new AdminOptions()),
            Options.Create(new ProxyServerOptions()),
            Options.Create(backends),
            NullLogger<RuntimeMappingStore>.Instance);
        var mapper = new ModelMapper(mappingStore);
        var compliance = new ComplianceLogWriter(NullLogger<ComplianceLogWriter>.Instance, Options.Create(new ComplianceLogOptions()));
        var classifier = new RuntimeClassifierStore(
            Options.Create(new ClassifierOptions()), Options.Create(new AdminOptions()), Options.Create(backends), Options.Create(new ProxyServerOptions()), NullLogger<RuntimeClassifierStore>.Instance);
        return new ProxyHandler(new NoopHttpClientFactory(), NullLogger<ProxyHandler>.Instance, mapper, compliance, classifier, Options.Create(backends));
    }

    private static string NewRuntimePath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mappings-runtime.json");
    private static string RuntimeFile(string path) => Path.Combine(Path.GetDirectoryName(path)!, "model-discovery-runtime.json");
    private static void Cleanup(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
