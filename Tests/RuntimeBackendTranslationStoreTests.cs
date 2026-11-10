using AiGateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class RuntimeBackendTranslationStoreTests
{
    [Fact]
    public void Store_ListsConfiguredBackendsInStableOrderAndRejectsUnknownName()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, CreateBackends());

            Assert.Collection(store.All,
                backend =>
                {
                    Assert.Equal("openrouter", backend.Name);
                    Assert.True(backend.TranslateAnthropicToOpenAI);
                    Assert.Equal("ChatCompletions", backend.OpenAIProtocol);
                    Assert.Equal("OpenRouter", backend.ReasoningFormat);
                    Assert.Equal("base", backend.Source);
                },
                backend =>
                {
                    Assert.Equal("secondary", backend.Name);
                    Assert.False(backend.TranslateAnthropicToOpenAI);
                    Assert.Equal("Responses", backend.OpenAIProtocol);
                    Assert.Equal("OpenAI", backend.ReasoningFormat);
                    Assert.Equal("base", backend.Source);
                });

            Assert.Throws<InvalidOperationException>(() => store.Get("missing"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Save_PersistsOverridesAsNamedValuesAndLoadsThemAfterRestart()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, CreateBackends());

            var saved = store.Save("openrouter", new BackendTranslationUpdate(true, "Responses", "OpenAI"));

            Assert.Equal("runtime", saved.Source);
            Assert.Equal("Responses", saved.OpenAIProtocol);
            Assert.Equal("OpenAI", saved.ReasoningFormat);
            Assert.True(File.Exists(RuntimeFile(path)));
            var json = File.ReadAllText(RuntimeFile(path));
            Assert.Contains("\"OpenAIProtocol\": \"Responses\"", json);
            Assert.Contains("\"ReasoningFormat\": \"OpenAI\"", json);

            var restarted = CreateStore(path, CreateBackends());
            var loaded = restarted.Get("OPENROUTER");
            Assert.Equal(saved with { Name = "openrouter" }, loaded);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Delete_RemovesOnlyTheNamedOverrideAndRestoresBaseValues()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, CreateBackends());
            store.Save("openrouter", new BackendTranslationUpdate(false, "ChatCompletions", "OpenAI"));
            store.Save("secondary", new BackendTranslationUpdate(true, "Responses", "OpenRouter"));

            var restored = store.Delete("OPENROUTER");

            Assert.Equal("base", restored.Source);
            Assert.True(restored.TranslateAnthropicToOpenAI);
            Assert.Equal("ChatCompletions", restored.OpenAIProtocol);
            Assert.Equal("OpenRouter", restored.ReasoningFormat);
            Assert.Equal("runtime", store.Get("secondary").Source);
            Assert.True(File.Exists(RuntimeFile(path)));

            var restarted = CreateStore(path, CreateBackends());
            Assert.Equal("base", restarted.Get("openrouter").Source);
            Assert.Equal("runtime", restarted.Get("secondary").Source);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Delete_LastOverride_RemovesRuntimeFile()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, CreateBackends());
            store.Save("openrouter", new BackendTranslationUpdate(false, "Responses", "OpenAI"));

            store.Delete("openrouter");

            Assert.False(File.Exists(RuntimeFile(path)));
            Assert.Equal("base", store.Get("openrouter").Source);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("backend-translations-runtime.json")]
    [InlineData("Backend-Translations-Runtime.json")]
    public void RuntimeFileCollision_PreservesConfiguredMappingFileWhenSavingAndDeletingOverrides(string configuredFileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configuredPath = Path.Combine(directory, configuredFileName);
        var translationPath = Path.Combine(directory, "backend-translations-runtime-overrides.json");
        const string sentinel = "mapping-runtime-sentinel";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(configuredPath, sentinel);
            var store = CreateStore(configuredPath, CreateBackends());

            store.Save("openrouter", new BackendTranslationUpdate(false, "Responses", "OpenAI"));

            Assert.Equal(sentinel, File.ReadAllText(configuredPath));
            Assert.True(File.Exists(translationPath));

            store.Delete("openrouter");

            Assert.Equal(sentinel, File.ReadAllText(configuredPath));
            Assert.False(File.Exists(translationPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Save_ReturnsItsCommittedView_WhenAnotherSaveCommitsBeforeItReturns()
    {
        var path = NewRuntimePath();
        using var logger = new BlockingLogger();
        Task<BackendTranslationView>? firstSave = null;
        BackendTranslationView? firstSavedView = null;
        try
        {
            var store = CreateStore(path, CreateBackends(), logger);
            logger.BlockNextInformation();
            firstSave = Task.Run(() => store.Save("openrouter",
                new BackendTranslationUpdate(false, "Responses", "OpenRouter")));

            Assert.True(logger.WaitUntilBlocked(TimeSpan.FromSeconds(5)), "The first save did not reach its post-commit log call.");

            var secondSave = store.Save("openrouter",
                new BackendTranslationUpdate(true, "ChatCompletions", "OpenAI"));
            Assert.Equal("ChatCompletions", secondSave.OpenAIProtocol);
        }
        finally
        {
            logger.Release();
            if (firstSave is not null)
                firstSavedView = await firstSave;
            Cleanup(path);
        }

        Assert.NotNull(firstSavedView);
        Assert.Equal("Responses", firstSavedView!.OpenAIProtocol);
        Assert.Equal("OpenRouter", firstSavedView.ReasoningFormat);
        Assert.False(firstSavedView.TranslateAnthropicToOpenAI);
    }

    [Fact]
    public async Task Delete_ReturnsBaseView_WhenAnotherSaveCommitsBeforeItReturns()
    {
        var path = NewRuntimePath();
        using var logger = new BlockingLogger();
        Task<BackendTranslationView>? deletion = null;
        BackendTranslationView? deletedView = null;
        try
        {
            var store = CreateStore(path, CreateBackends(), logger);
            store.Save("openrouter", new BackendTranslationUpdate(false, "Responses", "OpenAI"));
            logger.BlockNextInformation();
            deletion = Task.Run(() => store.Delete("openrouter"));

            Assert.True(logger.WaitUntilBlocked(TimeSpan.FromSeconds(5)), "Delete did not reach its post-commit log call.");

            var laterSave = store.Save("openrouter",
                new BackendTranslationUpdate(false, "ChatCompletions", "OpenRouter"));
            Assert.Equal("ChatCompletions", laterSave.OpenAIProtocol);
        }
        finally
        {
            logger.Release();
            if (deletion is not null)
                deletedView = await deletion;
            Cleanup(path);
        }

        Assert.NotNull(deletedView);
        Assert.Equal("base", deletedView!.Source);
        Assert.True(deletedView.TranslateAnthropicToOpenAI);
        Assert.Equal("ChatCompletions", deletedView.OpenAIProtocol);
        Assert.Equal("OpenRouter", deletedView.ReasoningFormat);
    }

    [Fact]
    public void Save_DoesNotPublishCandidateSnapshotWhenPersistenceFails()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, CreateBackends());
            var previousAll = store.All;
            Directory.CreateDirectory(RuntimeFile(path));

            var exception = Record.Exception(() => store.Save("openrouter",
                new BackendTranslationUpdate(false, "Responses", "OpenAI")));
            Assert.True(exception is IOException or UnauthorizedAccessException,
                $"Expected a file-system persistence failure, got {exception?.GetType().Name ?? "no exception"}.");

            Assert.Same(previousAll, store.All);
            Assert.Equal("base", store.Get("openrouter").Source);
            Assert.True(Directory.Exists(RuntimeFile(path)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Save_RejectsUnknownBackendAndIncompleteOrUnsupportedSettingsWithoutChangingStateOrFile()
    {
        var path = NewRuntimePath();
        try
        {
            var store = CreateStore(path, CreateBackends());
            store.Save("openrouter", new BackendTranslationUpdate(false, "Responses", "OpenAI"));
            var previousAll = store.All;
            var previousFile = File.ReadAllBytes(RuntimeFile(path));

            Assert.Throws<InvalidOperationException>(() =>
                store.Save("missing", new BackendTranslationUpdate(true, "Responses", "OpenAI")));
            Assert.Throws<InvalidOperationException>(() =>
                store.Save("openrouter", new BackendTranslationUpdate(null, "Responses", "OpenAI")));
            Assert.Throws<InvalidOperationException>(() =>
                store.Save("openrouter", new BackendTranslationUpdate(true, "Unsupported", "OpenAI")));
            Assert.Throws<InvalidOperationException>(() =>
                store.Save("openrouter", new BackendTranslationUpdate(true, "Responses", "Unsupported")));

            Assert.Same(previousAll, store.All);
            Assert.Equal(previousFile, File.ReadAllBytes(RuntimeFile(path)));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"openrouter\":{\"TranslateAnthropicToOpenAI\":true,\"OpenAIProtocol\":\"Unsupported\",\"ReasoningFormat\":\"OpenAI\"}}")]
    [InlineData("{\"unknown\":{\"TranslateAnthropicToOpenAI\":true,\"OpenAIProtocol\":\"Responses\",\"ReasoningFormat\":\"OpenAI\"}}")]
    public void Store_FallsBackToBaseWhenRuntimeFileIsInvalid(string json)
    {
        var path = NewRuntimePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimeFile(path))!);
            File.WriteAllText(RuntimeFile(path), json);

            var store = CreateStore(path, CreateBackends());

            Assert.Equal("base", store.Get("openrouter").Source);
            Assert.True(store.Get("openrouter").TranslateAnthropicToOpenAI);
            Assert.Equal("ChatCompletions", store.Get("openrouter").OpenAIProtocol);
        }
        finally { Cleanup(path); }
    }

    private static RuntimeBackendTranslationStore CreateStore(
        string path,
        BackendOptions backends,
        ILogger<RuntimeBackendTranslationStore>? logger = null) =>
        new(Options.Create(backends), Options.Create(new AdminOptions { RuntimeConfigPath = path }),
            logger ?? NullLogger<RuntimeBackendTranslationStore>.Instance);

    private static BackendOptions CreateBackends() => new()
    {
        ["secondary"] = new BackendConfig
        {
            BaseUrl = "https://secondary.example",
            TranslateAnthropicToOpenAI = false,
            OpenAIProtocol = OpenAIProtocol.Responses,
            ReasoningFormat = ReasoningFormat.OpenAI,
        },
        ["openrouter"] = new BackendConfig
        {
            BaseUrl = "https://openrouter.example",
            TranslateAnthropicToOpenAI = true,
            OpenAIProtocol = OpenAIProtocol.ChatCompletions,
            ReasoningFormat = ReasoningFormat.OpenRouter,
        },
    };

    private static string NewRuntimePath() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mappings-runtime.json");

    private static string RuntimeFile(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, "backend-translations-runtime.json");

    private static void Cleanup(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class BlockingLogger : ILogger<RuntimeBackendTranslationStore>, IDisposable
    {
        private readonly ManualResetEventSlim _blocked = new(false);
        private readonly ManualResetEventSlim _release = new(true);
        private int _blockNextInformation;

        public void BlockNextInformation()
        {
            _blocked.Reset();
            _release.Reset();
            Interlocked.Exchange(ref _blockNextInformation, 1);
        }

        public bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information && Interlocked.Exchange(ref _blockNextInformation, 0) == 1)
            {
                _blocked.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test did not release the blocked log call.");
            }
        }

        public void Dispose()
        {
            _blocked.Dispose();
            _release.Dispose();
        }
    }
}
