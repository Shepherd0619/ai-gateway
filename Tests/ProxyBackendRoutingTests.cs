using AiGateway.Configuration;
using AiGateway.Proxy;
using Xunit;

namespace AiGateway.Tests;

public sealed class ProxyBackendRoutingTests
{
    [Fact]
    public void BackendRoute_UsesBackendBaseUrlAndOriginalPath()
    {
        var route = BackendRoute.Create(
            new MapResult("local-model", "lmstudio", null),
            new BackendOptions
            {
                ["lmstudio"] = new BackendConfig { BaseUrl = "http://127.0.0.1:1234" },
            },
            "/v1/messages");

        Assert.Equal("backend-lmstudio", route.HttpClientName);
        Assert.Equal("http://127.0.0.1:1234/v1/messages", route.Url);
    }

    [Fact]
    public void BackendRoute_UsesBackendSpecificProxyClient()
    {
        var route = BackendRoute.Create(
            new MapResult("remote-model", "openrouter", "us-exit"),
            new BackendOptions
            {
                ["openrouter"] = new BackendConfig { BaseUrl = "https://openrouter.ai/api/" },
            },
            "/v1/messages");

        Assert.Equal("backend-openrouter-proxy-us-exit", route.HttpClientName);
        Assert.Equal("https://openrouter.ai/api/v1/messages", route.Url);
    }
}
