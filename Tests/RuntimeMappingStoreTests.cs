using AiGateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class RuntimeMappingStoreTests
{
    [Fact]
    public void RuntimeStore_PreservesBackendField()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = CreateStore(path, []);
            store.Save([
                new MappingRule { Prefix = "claude-local", Target = "local", Backend = "lmstudio" },
            ]);

            var json = File.ReadAllText(path);
            Assert.Contains("lmstudio", json);
            Assert.Equal("lmstudio", store.Rules.Single(r => r.Prefix == "claude-local").Backend);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RuntimeStore_RejectsUnknownBackend()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = CreateStore(path, []);

            var exception = Assert.Throws<InvalidOperationException>(() => store.Save([
                new MappingRule { Prefix = "claude", Target = "remote", Backend = "missing" },
            ]));

            Assert.Contains("missing", exception.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static RuntimeMappingStore CreateStore(string path, IReadOnlyList<MappingRule> rules)
    {
        return new RuntimeMappingStore(
            Options.Create(new ModelMappingOptions { Rules = rules.ToList() }),
            Options.Create(new AdminOptions { RuntimeConfigPath = path }),
            Options.Create(new ProxyServerOptions()),
            Options.Create(new BackendOptions
            {
                [BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = "https://example/api" },
                ["lmstudio"] = new BackendConfig { BaseUrl = "http://127.0.0.1:1234" },
            }),
            NullLogger<RuntimeMappingStore>.Instance);
    }
}
