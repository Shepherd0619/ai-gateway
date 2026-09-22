using System.Text.Json;
using AiGateway.Configuration;
using Xunit;

namespace AiGateway.Tests;

public sealed class BackendConfigurationTests
{
    [Fact]
    public void MappingRule_DeserializesOptionalBackend()
    {
        var rule = JsonSerializer.Deserialize<MappingRule>(
            "{\"Prefix\":\"claude-local\",\"Target\":\"local-model\",\"Backend\":\"lmstudio\"}")!;

        Assert.Equal("lmstudio", rule.Backend);
    }

    [Fact]
    public void MappingRule_AllowsBackendToBeOmitted()
    {
        var rule = JsonSerializer.Deserialize<MappingRule>(
            "{\"Prefix\":\"claude\",\"Target\":\"remote-model\"}")!;

        Assert.Null(rule.Backend);
    }

    [Fact]
    public void LegacyUpstreamConfigurationCreatesOpenRouterBackend()
    {
        var backends = BackendConfiguration.Normalize(
            new BackendOptions(),
            "https://legacy.example/api");

        Assert.Equal("https://legacy.example/api", backends[BackendOptions.DefaultBackendName].BaseUrl);
    }

    [Fact]
    public void ExplicitBackendsTakePrecedenceOverLegacyUpstream()
    {
        var configured = new BackendOptions
        {
            [BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = "https://configured.example/api" },
            ["lmstudio"] = new BackendConfig { BaseUrl = "http://127.0.0.1:1234" },
        };

        var backends = BackendConfiguration.Normalize(configured, "https://legacy.example/api");

        Assert.Equal("https://configured.example/api", backends[BackendOptions.DefaultBackendName].BaseUrl);
        Assert.Equal("http://127.0.0.1:1234", backends["lmstudio"].BaseUrl);
    }

    [Fact]
    public void ExplicitNonDefaultBackendStillAddsLegacyOpenRouter()
    {
        var backends = BackendConfiguration.Normalize(
            new BackendOptions
            {
                ["lmstudio"] = new BackendConfig { BaseUrl = "http://127.0.0.1:1234" },
            },
            "https://legacy.example/api");

        Assert.Equal("https://legacy.example/api", backends[BackendOptions.DefaultBackendName].BaseUrl);
        Assert.Equal("http://127.0.0.1:1234", backends["lmstudio"].BaseUrl);
    }

    [Fact]
    public void ValidateRulesRejectsUnknownBackend()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BackendConfiguration.ValidateRules(
                [new MappingRule { Prefix = "claude", Backend = "missing" }],
                new BackendOptions
                {
                    [BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = "https://example/api" },
                }));

        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    public void ValidateRulesAllowsLegacyRuleWithoutBackend()
    {
        BackendConfiguration.ValidateRules(
            [new MappingRule { Prefix = "claude", Target = "remote-model" }],
            new BackendOptions
            {
                [BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = "https://example/api" },
            });
    }
}
