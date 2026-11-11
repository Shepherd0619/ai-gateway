using System.Text.Json;
using AiGateway.Authentication;
using AiGateway.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiGateway.Tests;

public sealed class BackendCredentialStoreTests
{
    [Fact]
    public void ConfigurationBindsProviderKeyButPublicSerializationAndDiagnosticsDoNotExposeIt()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["openrouter:BaseUrl"]="https://test.example",["openrouter:ApiKey"]="injected-secret" }).Build().Get<BackendOptions>()!;
        Assert.Equal("injected-secret",new BackendCredentialStore(Options.Create(config)).GetKey("openrouter"));
        Assert.DoesNotContain("injected-secret",JsonSerializer.Serialize(config));
        Assert.DoesNotContain("injected-secret",config["openrouter"].ToString());
    }

    private static BackendCredentialStore Create() => new(Options.Create(new BackendOptions
    {
        ["openrouter"] = new() { BaseUrl = "https://example.test", ApiKey = "provider-secret" },
        ["custom/name"] = new() { BaseUrl = "https://other.test" }
    }));

    [Fact]
    public void OverrideIsCaseInsensitiveAndDeleteRestoresConfiguration()
    {
        var store = Create();
        Assert.Equal("provider-secret", store.GetKey("OPENROUTER"));
        var saved = store.Save("OPENROUTER", "temporary-secret");
        Assert.Equal("runtime", saved.Source);
        Assert.Equal("temporary-secret", store.GetKey("openrouter"));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(store.All));
        Assert.Equal("configuration", store.Delete("OPENROUTER").Source);
        Assert.Equal("provider-secret", store.GetKey("openrouter"));
        Assert.Equal("none", store.Delete("custom/name").Source);
        Assert.Null(store.GetKey("custom/name"));
        Assert.Equal("provider-secret", Create().GetKey("openrouter"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("secret\r\ninjected: header")]
    [InlineData("sk-aigw-not-a-provider-key")]
    [InlineData("SK-AIGW-reserved")]
    public void RejectsInvalidOrGatewayCredentials(string key)
    {
        Assert.Throws<ArgumentException>(() => Create().Save("openrouter", key));
        var options = Options.Create(new BackendOptions { ["openrouter"] = new() { ApiKey = key, BaseUrl = "https://test.example" } });
        if (!string.IsNullOrWhiteSpace(key))
            Assert.Throws<ArgumentException>(() => new BackendCredentialStore(options));
    }

    [Fact]
    public void UnknownBackendDoesNotCreateCredential()
    {
        var store = Create();
        Assert.Throws<KeyNotFoundException>(() => store.Save("missing", "secret"));
        Assert.Throws<KeyNotFoundException>(() => store.Delete("missing"));
    }
}
