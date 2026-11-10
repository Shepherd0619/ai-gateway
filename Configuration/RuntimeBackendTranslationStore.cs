using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AiGateway.Configuration;

internal sealed record BackendTranslationUpdate(
    bool? TranslateAnthropicToOpenAI,
    string? OpenAIProtocol,
    string? ReasoningFormat);

internal sealed record BackendTranslationView(
    string Name,
    bool TranslateAnthropicToOpenAI,
    string OpenAIProtocol,
    string ReasoningFormat,
    string Source);

internal sealed class RuntimeBackendTranslationStore
{
    private const string RuntimeFileName = "backend-translations-runtime.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly BackendOptions _baseOptions;
    private readonly Dictionary<string, string> _backendNames;
    private readonly string _runtimePath;
    private readonly ILogger<RuntimeBackendTranslationStore> _logger;
    private readonly object _writeLock = new();
    private volatile StoreSnapshot _snapshot;

    public IReadOnlyList<BackendTranslationView> All => _snapshot.All;

    public RuntimeBackendTranslationStore(
        IOptions<BackendOptions> backendOptions,
        IOptions<AdminOptions> adminOptions,
        ILogger<RuntimeBackendTranslationStore> logger)
    {
        _baseOptions = new BackendOptions();
        foreach (var (name, config) in backendOptions.Value)
            _baseOptions[name] = config;
        _backendNames = _baseOptions.Keys.ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);
        _runtimePath = ResolveRuntimePath(adminOptions.Value.RuntimeConfigPath);
        _logger = logger;

        ValidateBaseOptions();
        _snapshot = CreateSnapshot(LoadOverrides());
    }

    public BackendTranslationView Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_snapshot.ByName.TryGetValue(name, out var view))
            throw UnknownBackend(name);

        return view;
    }

    public BackendTranslationView Save(string name, BackendTranslationUpdate update)
    {
        var canonicalName = GetCanonicalName(name);
        ArgumentNullException.ThrowIfNull(update);
        var validated = ValidateUpdate(update);

        BackendTranslationView savedView;
        lock (_writeLock)
        {
            var candidate = CopyOverrides(_snapshot.Overrides);
            candidate[canonicalName] = validated;
            ValidateOverrides(candidate);
            var newSnapshot = CreateSnapshot(candidate);
            WriteRuntimeFile(candidate);
            _snapshot = newSnapshot;
            savedView = newSnapshot.ByName[canonicalName];
        }

        _logger.LogInformation("Runtime backend translation saved: {Backend} to {Path}", canonicalName, _runtimePath);
        return savedView;
    }

    public BackendTranslationView Delete(string name)
    {
        var canonicalName = GetCanonicalName(name);

        BackendTranslationView baseView;
        lock (_writeLock)
        {
            var candidate = CopyOverrides(_snapshot.Overrides);
            candidate.Remove(canonicalName);
            ValidateOverrides(candidate);
            var newSnapshot = CreateSnapshot(candidate);

            if (candidate.Count == 0)
                File.Delete(_runtimePath);
            else
                WriteRuntimeFile(candidate);

            _snapshot = newSnapshot;
            baseView = newSnapshot.ByName[canonicalName];
        }

        _logger.LogInformation("Runtime backend translation deleted: {Backend}; reverted to base configuration", canonicalName);
        return baseView;
    }

    private static string ResolveRuntimePath(string configuredRuntimePath)
    {
        var absoluteConfiguredPath = Path.GetFullPath(configuredRuntimePath);
        var directory = Path.GetDirectoryName(absoluteConfiguredPath) ?? ".";
        var runtimePath = Path.Combine(directory, RuntimeFileName);
        if (!string.Equals(runtimePath, absoluteConfiguredPath, StringComparison.OrdinalIgnoreCase))
            return runtimePath;

        var fallbackStem = Path.Combine(directory, "backend-translations-runtime-overrides");
        var fallbackPath = fallbackStem + ".json";
        for (var suffix = 1; string.Equals(fallbackPath, absoluteConfiguredPath, StringComparison.OrdinalIgnoreCase); suffix++)
            fallbackPath = $"{fallbackStem}-{suffix}.json";
        return fallbackPath;
    }

    private Dictionary<string, RuntimeBackendTranslationOverride> LoadOverrides()
    {
        if (!File.Exists(_runtimePath))
            return new Dictionary<string, RuntimeBackendTranslationOverride>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_runtimePath);
            var persisted = JsonSerializer.Deserialize<Dictionary<string, RuntimeBackendTranslationOverride>>(json, SerializerOptions)
                ?? throw new InvalidDataException("Runtime backend translation config must be a JSON object.");
            var overrides = new Dictionary<string, RuntimeBackendTranslationOverride>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in persisted)
            {
                var canonicalName = GetCanonicalName(name);
                if (value is null || !overrides.TryAdd(canonicalName, value))
                    throw new InvalidDataException($"Runtime backend translation config contains an invalid or duplicate entry for '{name}'.");
            }

            ValidateOverrides(overrides);
            return overrides;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load backend translation runtime config from {Path}, using base configuration", _runtimePath);
            return new Dictionary<string, RuntimeBackendTranslationOverride>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteRuntimeFile(Dictionary<string, RuntimeBackendTranslationOverride> overrides)
    {
        var directory = Path.GetDirectoryName(_runtimePath);
        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _runtimePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(overrides, SerializerOptions);
            File.WriteAllText(tempPath, json);
            // Keep the temporary file beside the destination so File.Move uses same-volume replacement,
            // rather than its documented cross-volume copy-and-delete behavior.
            File.Move(tempPath, _runtimePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to remove temporary backend translation config file {Path}", tempPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Failed to remove temporary backend translation config file {Path}", tempPath);
            }
        }
    }

    private StoreSnapshot CreateSnapshot(Dictionary<string, RuntimeBackendTranslationOverride> overrides)
    {
        var views = _baseOptions
            .OrderBy(backend => backend.Key, StringComparer.OrdinalIgnoreCase)
            .Select(backend =>
            {
                if (overrides.TryGetValue(backend.Key, out var runtime))
                {
                    return new BackendTranslationView(
                        backend.Key,
                        runtime.TranslateAnthropicToOpenAI,
                        runtime.OpenAIProtocol.ToString(),
                        runtime.ReasoningFormat.ToString(),
                        "runtime");
                }

                return new BackendTranslationView(
                    backend.Key,
                    backend.Value.TranslateAnthropicToOpenAI,
                    backend.Value.OpenAIProtocol.ToString(),
                    backend.Value.ReasoningFormat.ToString(),
                    "base");
            })
            .ToArray();

        var readOnlyViews = Array.AsReadOnly(views);
        var byName = views.ToDictionary(view => view.Name, StringComparer.OrdinalIgnoreCase);
        return new StoreSnapshot(overrides, readOnlyViews, byName);
    }

    private void ValidateBaseOptions()
    {
        foreach (var (name, backend) in _baseOptions)
        {
            if (!Enum.IsDefined(backend.OpenAIProtocol))
                throw new InvalidOperationException($"Backend '{name}' has an unsupported OpenAIProtocol.");
            if (!Enum.IsDefined(backend.ReasoningFormat))
                throw new InvalidOperationException($"Backend '{name}' has an unsupported ReasoningFormat.");
        }
    }

    private RuntimeBackendTranslationOverride ValidateUpdate(BackendTranslationUpdate update)
    {
        if (update.TranslateAnthropicToOpenAI is null)
            throw new InvalidOperationException("TranslateAnthropicToOpenAI is required.");

        return new RuntimeBackendTranslationOverride
        {
            TranslateAnthropicToOpenAI = update.TranslateAnthropicToOpenAI.Value,
            OpenAIProtocol = ParseEnum<OpenAIProtocol>(update.OpenAIProtocol, "OpenAIProtocol"),
            ReasoningFormat = ParseEnum<ReasoningFormat>(update.ReasoningFormat, "ReasoningFormat"),
        };
    }

    private void ValidateOverrides(Dictionary<string, RuntimeBackendTranslationOverride> overrides)
    {
        foreach (var (name, value) in overrides)
        {
            _ = GetCanonicalName(name);
            if (value is null)
                throw new InvalidOperationException($"Runtime translation settings for backend '{name}' must not be null.");
            if (!Enum.IsDefined(value.OpenAIProtocol))
                throw new InvalidOperationException($"Backend '{name}' has an unsupported OpenAIProtocol.");
            if (!Enum.IsDefined(value.ReasoningFormat))
                throw new InvalidOperationException($"Backend '{name}' has an unsupported ReasoningFormat.");
        }
    }

    private string GetCanonicalName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && _backendNames.TryGetValue(name, out var canonicalName))
            return canonicalName;

        throw UnknownBackend(name);
    }

    private static TEnum ParseEnum<TEnum>(string? value, string propertyName)
        where TEnum : struct, Enum
    {
        if (value is not null
            && Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            return parsed;
        }

        throw new InvalidOperationException($"{propertyName} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static Dictionary<string, RuntimeBackendTranslationOverride> CopyOverrides(
        IReadOnlyDictionary<string, RuntimeBackendTranslationOverride> overrides)
    {
        var copy = new Dictionary<string, RuntimeBackendTranslationOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overrides)
            copy.Add(name, value);
        return copy;
    }

    private static InvalidOperationException UnknownBackend(string? name) =>
        new($"Backend '{name}' is not defined in the Backends configuration.");

    private sealed record RuntimeBackendTranslationOverride
    {
        public required bool TranslateAnthropicToOpenAI { get; init; }
        public required OpenAIProtocol OpenAIProtocol { get; init; }
        public required ReasoningFormat ReasoningFormat { get; init; }
    }

    private sealed record StoreSnapshot(
        IReadOnlyDictionary<string, RuntimeBackendTranslationOverride> Overrides,
        IReadOnlyList<BackendTranslationView> All,
        IReadOnlyDictionary<string, BackendTranslationView> ByName);
}
