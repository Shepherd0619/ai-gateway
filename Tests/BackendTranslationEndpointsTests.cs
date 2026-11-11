using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiGateway.Admin;
using AiGateway.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class BackendTranslationEndpointsTests
{
    [Fact]
    public async Task BackendTranslationRoutes_RequireAuthenticationAndManageRuntimeOverrides()
    {
        await using var host = await TestHost.StartAsync();

        using var unauthorized = await host.Client.GetAsync("/admin/backend-translations");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/backend-translations");
        request.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var configured = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        using (var configuredJson = JsonDocument.Parse(await configured.Content.ReadAsStringAsync()))
        {
            var backends = configuredJson.RootElement.GetProperty("backends");
            Assert.Equal(6, backends.GetArrayLength());
            var openrouter = backends.EnumerateArray().Single(backend =>
                backend.GetProperty("name").GetString() == "openrouter");
            Assert.True(openrouter.GetProperty("translateAnthropicToOpenAI").GetBoolean());
            Assert.Equal("ChatCompletions", openrouter.GetProperty("openAIProtocol").GetString());
            Assert.Equal("OpenRouter", openrouter.GetProperty("reasoningFormat").GetString());
            Assert.Equal("base", openrouter.GetProperty("source").GetString());
        }

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/backend-translations/openrouter")
        {
            Content = JsonContent.Create(new BackendTranslationUpdate(false, "Responses", "OpenAI")),
        };
        updateRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var updated = await host.Client.SendAsync(updateRequest);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using (var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync()))
        {
            Assert.Equal("openrouter", updatedJson.RootElement.GetProperty("name").GetString());
            Assert.False(updatedJson.RootElement.GetProperty("translateAnthropicToOpenAI").GetBoolean());
            Assert.Equal("Responses", updatedJson.RootElement.GetProperty("openAIProtocol").GetString());
            Assert.Equal("OpenAI", updatedJson.RootElement.GetProperty("reasoningFormat").GetString());
            Assert.Equal("runtime", updatedJson.RootElement.GetProperty("source").GetString());
        }

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/backend-translations/openrouter")
        {
            Content = JsonContent.Create(new BackendTranslationUpdate(true, "Unsupported", "OpenAI")),
        };
        invalidRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var invalid = await host.Client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var invalidJson = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
            Assert.True(invalidJson.RootElement.TryGetProperty("error", out _));

        using var unknownRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/backend-translations/missing")
        {
            Content = JsonContent.Create(new BackendTranslationUpdate(true, "Responses", "OpenAI")),
        };
        unknownRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var unknown = await host.Client.SendAsync(unknownRequest);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using (var unknownJson = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()))
            Assert.True(unknownJson.RootElement.TryGetProperty("error", out _));

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/admin/backend-translations/openrouter");
        deleteRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var deleted = await host.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var getAfterDeleteRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/backend-translations");
        getAfterDeleteRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var restored = await host.Client.SendAsync(getAfterDeleteRequest);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var restoredJson = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
        var restoredOpenrouter = restoredJson.RootElement.GetProperty("backends").EnumerateArray().Single(backend =>
            backend.GetProperty("name").GetString() == "openrouter");
        Assert.Equal("base", restoredOpenrouter.GetProperty("source").GetString());
        Assert.True(restoredOpenrouter.GetProperty("translateAnthropicToOpenAI").GetBoolean());
        Assert.Equal("ChatCompletions", restoredOpenrouter.GetProperty("openAIProtocol").GetString());
    }

    [Theory]
    [InlineData("custom backend/blue")]
    [InlineData("literal%backend")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task BackendTranslationRoutes_ManageNamesThroughQueryFallback(string backendName)
    {
        await using var host = await TestHost.StartAsync();
        var queryPath = "/admin/backend-translations?name=" + Uri.EscapeDataString(backendName);
        var update = new BackendTranslationUpdate(false, "Responses", "OpenAI");

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, queryPath)
        {
            Content = JsonContent.Create(update),
        };
        updateRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var updated = await host.Client.SendAsync(updateRequest);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using (var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync()))
        {
            Assert.Equal(backendName, updatedJson.RootElement.GetProperty("name").GetString());
            Assert.Equal("runtime", updatedJson.RootElement.GetProperty("source").GetString());
            Assert.Equal("Responses", updatedJson.RootElement.GetProperty("openAIProtocol").GetString());
        }

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, queryPath);
        deleteRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var deleted = await host.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/backend-translations");
        listRequest.Headers.Add("x-admin-key", TestHost.AdminKey);
        using var listed = await host.Client.SendAsync(listRequest);
        using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var restored = listedJson.RootElement.GetProperty("backends").EnumerateArray().Single(backend =>
            backend.GetProperty("name").GetString() == backendName);
        Assert.Equal("base", restored.GetProperty("source").GetString());
    }

    private sealed class TestHost : IAsyncDisposable
    {
        public const string AdminKey = "test-admin-key";

        private readonly WebApplication _application;
        private readonly string _runtimeDirectory;

        public HttpClient Client { get; }

        private TestHost(WebApplication application, HttpClient client, string runtimeDirectory)
        {
            _application = application;
            Client = client;
            _runtimeDirectory = runtimeDirectory;
        }

        public static async Task<TestHost> StartAsync()
        {
            var runtimeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var runtimePath = Path.Combine(runtimeDirectory, "mappings-runtime.json");
            var backendOptions = new BackendOptions
            {
                ["openrouter"] = new BackendConfig
                {
                    BaseUrl = "https://openrouter.example/api",
                    TranslateAnthropicToOpenAI = true,
                    OpenAIProtocol = OpenAIProtocol.ChatCompletions,
                    ReasoningFormat = ReasoningFormat.OpenRouter,
                },
                ["secondary"] = new BackendConfig
                {
                    BaseUrl = "https://secondary.example/api",
                    TranslateAnthropicToOpenAI = false,
                    OpenAIProtocol = OpenAIProtocol.Responses,
                    ReasoningFormat = ReasoningFormat.OpenAI,
                },
                ["custom backend/blue"] = new BackendConfig
                {
                    BaseUrl = "https://slash.example/api",
                    TranslateAnthropicToOpenAI = true,
                    OpenAIProtocol = OpenAIProtocol.ChatCompletions,
                    ReasoningFormat = ReasoningFormat.OpenRouter,
                },
                ["literal%backend"] = new BackendConfig
                {
                    BaseUrl = "https://percent.example/api",
                    TranslateAnthropicToOpenAI = true,
                    OpenAIProtocol = OpenAIProtocol.ChatCompletions,
                    ReasoningFormat = ReasoningFormat.OpenRouter,
                },
                ["."] = new BackendConfig
                {
                    BaseUrl = "https://dot.example/api",
                    TranslateAnthropicToOpenAI = true,
                    OpenAIProtocol = OpenAIProtocol.ChatCompletions,
                    ReasoningFormat = ReasoningFormat.OpenRouter,
                },
                [".."] = new BackendConfig
                {
                    BaseUrl = "https://dotdot.example/api",
                    TranslateAnthropicToOpenAI = true,
                    OpenAIProtocol = OpenAIProtocol.ChatCompletions,
                    ReasoningFormat = ReasoningFormat.OpenRouter,
                },
            };
            var adminOptions = new AdminOptions { ApiKey = AdminKey, RuntimeConfigPath = runtimePath };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath(),
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton<IOptions<BackendOptions>>(Options.Create(backendOptions));
            builder.Services.AddSingleton<IOptions<AdminOptions>>(Options.Create(adminOptions));
            builder.Services.AddSingleton<IOptions<ModelMappingOptions>>(Options.Create(new ModelMappingOptions()));
            builder.Services.AddSingleton<IOptions<ClassifierOptions>>(Options.Create(new ClassifierOptions()));
            builder.Services.AddSingleton<IOptions<ModelDiscoveryOptions>>(Options.Create(new ModelDiscoveryOptions()));
            builder.Services.AddSingleton<IOptions<ProxyServerOptions>>(Options.Create(new ProxyServerOptions()));
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<RuntimeMappingStore>();
            builder.Services.AddSingleton<RuntimeClassifierStore>();
            builder.Services.AddSingleton<RuntimeModelDiscoveryStore>();
            builder.Services.AddSingleton<RuntimeBackendTranslationStore>();
            builder.Services.AddSingleton<AiGateway.Authentication.BackendCredentialStore>();

            var application = builder.Build();
            application.MapAdminEndpoints();
            await application.StartAsync();

            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()!.Addresses;
            var address = addresses.SingleOrDefault(value =>
                value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No IPv4 loopback address was published: {string.Join(", ", addresses)}");
            var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(address),
            };
            return new TestHost(application, client, runtimeDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
            if (Directory.Exists(_runtimeDirectory))
                Directory.Delete(_runtimeDirectory, recursive: true);
        }
    }
}
