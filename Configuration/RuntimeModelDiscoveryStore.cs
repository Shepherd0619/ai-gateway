using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiGateway.Configuration;

internal sealed record ModelDiscoverySnapshot(bool Enabled, IReadOnlyList<ModelDiscoveryModel> Models);

internal sealed class RuntimeModelDiscoveryStore
{
    private readonly ModelDiscoveryOptions _baseOptions;
    private readonly string _runtimePath;
    private readonly ILogger<RuntimeModelDiscoveryStore> _logger;
    private readonly object _writeLock = new();
    private volatile ModelDiscoverySnapshot _snapshot;

    public ModelDiscoverySnapshot Snapshot => _snapshot;

    public RuntimeModelDiscoveryStore(
        IOptions<ModelDiscoveryOptions> baseOptions,
        IOptions<AdminOptions> adminOptions,
        ILogger<RuntimeModelDiscoveryStore> logger)
    {
        _baseOptions = Normalize(baseOptions.Value);
        _runtimePath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(adminOptions.Value.RuntimeConfigPath)) ?? ".",
            "model-discovery-runtime.json");
        _logger = logger;
        Validate(_baseOptions);
        _snapshot = LoadSnapshot();
    }

    public ModelDiscoverySnapshot Save(ModelDiscoveryOptions options)
    {
        var normalized = Normalize(options);
        Validate(normalized);
        lock (_writeLock)
        {
            WriteRuntimeFile(normalized);
            _snapshot = ToSnapshot(normalized);
        }
        _logger.LogInformation("Runtime model discovery settings saved: enabled={Enabled}, models={Count}, path={Path}",
            normalized.Enabled, normalized.Models.Count, _runtimePath);
        return Snapshot;
    }

    public ModelDiscoverySnapshot Delete()
    {
        lock (_writeLock)
        {
            if (File.Exists(_runtimePath))
                File.Delete(_runtimePath);
            _snapshot = ToSnapshot(_baseOptions);
        }
        _logger.LogInformation("Runtime model discovery settings deleted; reverted to base configuration");
        return Snapshot;
    }

    private ModelDiscoverySnapshot LoadSnapshot()
    {
        if (!File.Exists(_runtimePath))
            return ToSnapshot(_baseOptions);
        try
        {
            var json = File.ReadAllText(_runtimePath);
            var runtime = JsonSerializer.Deserialize<ModelDiscoveryOptions>(json) ?? new ModelDiscoveryOptions();
            var normalized = Normalize(runtime);
            Validate(normalized);
            return ToSnapshot(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load model discovery runtime config from {Path}, using base configuration", _runtimePath);
            return ToSnapshot(_baseOptions);
        }
    }

    private void WriteRuntimeFile(ModelDiscoveryOptions options)
    {
        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
        var directory = Path.GetDirectoryName(Path.GetFullPath(_runtimePath));
        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        var tempPath = _runtimePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _runtimePath, overwrite: true);
    }

    private static ModelDiscoveryOptions Normalize(ModelDiscoveryOptions options)
    {
        if (options.Models?.Any(model => model is null) == true)
            throw new InvalidOperationException("Model discovery model entries must not be null.");

        return options with
        {
            Models = (options.Models ?? []).Select(model => model with { Id = model.Id?.Trim() ?? "" }).ToList(),
        };
    }

    private static void Validate(ModelDiscoveryOptions options)
    {
        foreach (var model in options.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
                throw new InvalidOperationException("Model discovery model IDs must not be blank.");
        }

        var duplicate = options.Models
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate model discovery ID '{duplicate.Key}'. IDs must be unique ignoring case.");
    }

    private ModelDiscoverySnapshot ToSnapshot(ModelDiscoveryOptions options) =>
        new(options.Enabled, Merge(options.Models, _baseOptions.Models));

    private static IReadOnlyList<ModelDiscoveryModel> Merge(
        IReadOnlyList<ModelDiscoveryModel> runtimeModels,
        IReadOnlyList<ModelDiscoveryModel> baseModels)
    {
        var merged = new List<ModelDiscoveryModel>(runtimeModels.Count + baseModels.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in runtimeModels)
        {
            if (seen.Add(model.Id))
                merged.Add(model);
        }
        foreach (var model in baseModels)
        {
            if (seen.Add(model.Id))
                merged.Add(model);
        }
        return merged;
    }
}
