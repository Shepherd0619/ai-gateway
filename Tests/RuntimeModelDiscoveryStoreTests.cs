using AiGateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class RuntimeModelDiscoveryStoreTests
{
    [Fact]
    public void Store_UsesBaseCatalogAndDisabledDefault()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, new ModelDiscoveryOptions
            {
                Models = [new ModelDiscoveryModel { Id = "base-model" }],
            });

            Assert.False(store.Snapshot.Enabled);
            Assert.Equal("base-model", Assert.Single(store.Snapshot.Models).Id);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Save_MergesRuntimeModelsFirstAndOverridesIdsCaseInsensitively()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, new ModelDiscoveryOptions
            {
                Models = [
                    new ModelDiscoveryModel { Id = "claude-sonnet", DisplayName = "Base name" },
                    new ModelDiscoveryModel { Id = "base-only" },
                ],
            });

            store.Save(new ModelDiscoveryOptions
            {
                Enabled = true,
                Models = [new ModelDiscoveryModel { Id = "CLAUDE-SONNET", DisplayName = "Runtime name" }],
            });

            Assert.True(store.Snapshot.Enabled);
            Assert.Collection(store.Snapshot.Models,
                model => { Assert.Equal("CLAUDE-SONNET", model.Id); Assert.Equal("Runtime name", model.DisplayName); },
                model => Assert.Equal("base-only", model.Id));
            Assert.True(File.Exists(RuntimeFile(path)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Save_RejectsBlankOrDuplicateIdsWithoutPublishingOrPersisting()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, new ModelDiscoveryOptions { Models = [new ModelDiscoveryModel { Id = "base" }] });
            var before = store.Snapshot;

            Assert.Throws<InvalidOperationException>(() => store.Save(new ModelDiscoveryOptions
            {
                Enabled = true,
                Models = [new ModelDiscoveryModel { Id = " " }],
            }));
            Assert.Throws<InvalidOperationException>(() => store.Save(new ModelDiscoveryOptions
            {
                Enabled = true,
                Models = [new ModelDiscoveryModel { Id = "same" }, new ModelDiscoveryModel { Id = "SAME" }],
            }));

            Assert.Same(before, store.Snapshot);
            Assert.False(File.Exists(RuntimeFile(path)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Delete_RestoresBaseSettingsAndModels()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, new ModelDiscoveryOptions
            {
                Enabled = true,
                Models = [new ModelDiscoveryModel { Id = "base" }],
            });
            store.Save(new ModelDiscoveryOptions { Enabled = false, Models = [new ModelDiscoveryModel { Id = "runtime" }] });

            store.Delete();

            Assert.True(store.Snapshot.Enabled);
            Assert.Equal("base", Assert.Single(store.Snapshot.Models).Id);
            Assert.False(File.Exists(RuntimeFile(path)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Store_RejectsDuplicateBaseIdsAtConstruction()
    {
        var path = NewRuntimePath();
        try
        {
            Assert.Throws<InvalidOperationException>(() => CreateStore(path, new ModelDiscoveryOptions
            {
                Models = [new ModelDiscoveryModel { Id = "same" }, new ModelDiscoveryModel { Id = "SAME" }],
            }));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Store_FallsBackToBaseWhenRuntimeFileIsInvalid()
    {
        var path = NewRuntimePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimeFile(path))!);
            File.WriteAllText(RuntimeFile(path), "not json");
            var store = CreateStore(path, new ModelDiscoveryOptions { Models = [new ModelDiscoveryModel { Id = "base" }] });

            Assert.False(store.Snapshot.Enabled);
            Assert.Equal("base", Assert.Single(store.Snapshot.Models).Id);
        }
        finally { Cleanup(path); }
    }

    private static RuntimeModelDiscoveryStore CreateStore(string path, ModelDiscoveryOptions options) =>
        new(Options.Create(options), Options.Create(new AdminOptions { RuntimeConfigPath = path }), NullLogger<RuntimeModelDiscoveryStore>.Instance);

    private static string NewRuntimePath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mappings-runtime.json");
    private static string RuntimeFile(string path) => Path.Combine(Path.GetDirectoryName(path)!, "model-discovery-runtime.json");
    private static void Cleanup(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
