using AiGateway.Configuration;
using AiGateway.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class ModelMapperTests
{
    private static ModelMapper CreateMapper(IReadOnlyList<MappingRule> rules)
    {
        var store = new RuntimeMappingStore(
            Options.Create(new ModelMappingOptions { Rules = rules.ToList() }),
            Options.Create(new AdminOptions { RuntimeConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json") }),
            Options.Create(new ProxyServerOptions()),
            NullLogger<RuntimeMappingStore>.Instance);
        return new ModelMapper(store);
    }

    [Fact]
    public void Map_FollowsChainedMappingsAndInheritsProxyFromLaterRule()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "claude-opus", Target = "openai/gpt-5.6-luna" },
            new MappingRule { Prefix = "openai/", Target = "router/openai/gpt-5.6-luna" },
            new MappingRule { Prefix = "router/", ProxyServer = "us-exit", Target = "groq/llama-4" },
            new MappingRule { Prefix = "groq/", ProxyServer = "eu-exit" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("claude-opus");

        Assert.Equal("groq/llama-4", result.TargetModel);
        Assert.Equal("us-exit", result.ProxyServer);
    }

    [Fact]
    public void Map_InheritsProxyFromRemappedOpenAiModel()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "openai/", ProxyServer = "us-exit" },
            new MappingRule { Prefix = "claude-opus", Target = "openai/gpt-5.6-luna" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("claude-opus");

        Assert.Equal("openai/gpt-5.6-luna", result.TargetModel);
        Assert.Equal("us-exit", result.ProxyServer);
    }

    [Fact]
    public void Map_DoesNotInheritProxyWhenRemappedModelDoesNotMatchProxyRule()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "openai/", ProxyServer = "us-exit" },
            new MappingRule { Prefix = "claude-opus", Target = "google/gemini-pro" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("claude-opus");

        Assert.Equal("google/gemini-pro", result.TargetModel);
        Assert.Null(result.ProxyServer);
    }

    [Fact]
    public void Map_PreservesFirstProxyServerAcrossMappings()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "claude-opus", Target = "openai/gpt-5.6-luna", ProxyServer = "eu-exit" },
            new MappingRule { Prefix = "openai/", ProxyServer = "us-exit" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("claude-opus");

        Assert.Equal("openai/gpt-5.6-luna", result.TargetModel);
        Assert.Equal("eu-exit", result.ProxyServer);
    }

    [Fact]
    public void Map_StopsAtEmptyTargetAfterApplyingProxy()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "openai/", ProxyServer = "us-exit" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("openai/gpt-5.6-luna");

        Assert.Equal("openai/gpt-5.6-luna", result.TargetModel);
        Assert.Equal("us-exit", result.ProxyServer);
    }

    [Fact]
    public void Map_ReturnsOriginalModelWithoutProxyWhenNoRuleMatches()
    {
        var mapper = CreateMapper(
        [
            new MappingRule { Prefix = "claude", Target = "openai/model" },
        ]);

        var result = mapper.Map("google/model");

        Assert.Equal("google/model", result.TargetModel);
        Assert.Null(result.ProxyServer);
    }

    [Fact]
    public void Map_MatchesPrefixesCaseInsensitively()
    {
        var mapper = CreateMapper(
        [
            new MappingRule { Prefix = "openai/", ProxyServer = "us-exit" },
        ]);

        var result = mapper.Map("OpenAI/GPT-5");

        Assert.Equal("OpenAI/GPT-5", result.TargetModel);
        Assert.Equal("us-exit", result.ProxyServer);
    }

    [Fact]
    public void Map_StopsOnMappingCycle()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "a/", Target = "b/model" },
            new MappingRule { Prefix = "b/", Target = "a/model" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("a/model");

        Assert.Equal("a/model", result.TargetModel);
    }

    [Fact]
    public void Map_StopsOnCaseInsensitiveMappingCycle()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "a/", Target = "B/model" },
            new MappingRule { Prefix = "b/", Target = "A/model" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("a/model");

        Assert.Equal("A/model", result.TargetModel);
    }

    [Fact]
    public void Map_PreservesFirstMatchWinsForOverlappingPrefixes()
    {
        var rules = new[]
        {
            new MappingRule { Prefix = "claude", Target = "openai/general" },
            new MappingRule { Prefix = "claude-opus", Target = "openai/specific" },
        };
        var mapper = CreateMapper(rules);

        var result = mapper.Map("claude-opus");

        Assert.Equal("openai/general", result.TargetModel);
    }

    [Fact]
    public void ClassifierStore_UsesBaseAndRestoresAfterRuntimeDelete()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var admin = Options.Create(new AdminOptions { RuntimeConfigPath = path });
        var store = new RuntimeClassifierStore(
            Options.Create(new ClassifierOptions { TargetModel = "base/model" }),
            admin,
            NullLogger<RuntimeClassifierStore>.Instance);

        try
        {
            Assert.Equal("base/model", store.Snapshot.TargetModel);
            Assert.Equal("base", store.Snapshot.Source);

            store.Save(" third-party/model ");
            Assert.Equal("third-party/model", store.Snapshot.TargetModel);
            Assert.Equal("runtime", store.Snapshot.Source);

            store.Delete();
            Assert.Equal("base/model", store.Snapshot.TargetModel);
            Assert.Equal("base", store.Snapshot.Source);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var classifierPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "classifier-runtime.json");
            if (File.Exists(classifierPath)) File.Delete(classifierPath);
        }
    }

    [Fact]
    public void ClassifierStore_NormalizesEmptyTargetToDisabledRuntimeSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new RuntimeClassifierStore(
            Options.Create(new ClassifierOptions { TargetModel = "base/model" }),
            Options.Create(new AdminOptions { RuntimeConfigPath = path }),
            NullLogger<RuntimeClassifierStore>.Instance);

        try
        {
            store.Save("   ");
            Assert.Null(store.Snapshot.TargetModel);
            Assert.False(store.Snapshot.Enabled);
            Assert.Equal("runtime", store.Snapshot.Source);
        }
        finally
        {
            var classifierPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "classifier-runtime.json");
            if (File.Exists(classifierPath)) File.Delete(classifierPath);
        }
    }
}
