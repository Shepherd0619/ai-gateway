using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiGateway.Configuration;

internal sealed record ClassifierSnapshot(string? TargetModel, string Source)
{
    public bool Enabled => !string.IsNullOrEmpty(TargetModel);
}

internal sealed class RuntimeClassifierStore
{
    private readonly string? _baseTargetModel;
    private readonly string _runtimeConfigPath;
    private readonly ILogger<RuntimeClassifierStore> _logger;
    private volatile ClassifierSnapshot _snapshot;

    public ClassifierSnapshot Snapshot => _snapshot;

    public RuntimeClassifierStore(
        IOptions<ClassifierOptions> baseOptions,
        IOptions<AdminOptions> adminOptions,
        ILogger<RuntimeClassifierStore> logger)
    {
        _baseTargetModel = Normalize(baseOptions.Value.TargetModel);
        _runtimeConfigPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(adminOptions.Value.RuntimeConfigPath)) ?? ".",
            "classifier-runtime.json");
        _logger = logger;
        _snapshot = LoadSnapshot();
    }

    public ClassifierSnapshot Save(string? targetModel)
    {
        var normalized = Normalize(targetModel);
        WriteRuntimeFile(normalized);
        _snapshot = new ClassifierSnapshot(normalized, "runtime");
        _logger.LogInformation("Runtime classifier saved: {TargetModel} to {Path}", normalized ?? "(disabled)", _runtimeConfigPath);
        return _snapshot;
    }

    public ClassifierSnapshot Delete()
    {
        if (File.Exists(_runtimeConfigPath))
            File.Delete(_runtimeConfigPath);

        _snapshot = new ClassifierSnapshot(_baseTargetModel, "base");
        _logger.LogInformation("Runtime classifier deleted; reverted to base configuration");
        return _snapshot;
    }

    private ClassifierSnapshot LoadSnapshot()
    {
        if (!File.Exists(_runtimeConfigPath))
            return new ClassifierSnapshot(_baseTargetModel, "base");

        try
        {
            var json = File.ReadAllText(_runtimeConfigPath);
            var runtime = JsonSerializer.Deserialize<RuntimeClassifierConfig>(json);
            return new ClassifierSnapshot(Normalize(runtime?.TargetModel), "runtime");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load classifier runtime config from {Path}, using base configuration", _runtimeConfigPath);
            return new ClassifierSnapshot(_baseTargetModel, "base");
        }
    }

    private void WriteRuntimeFile(string? targetModel)
    {
        var json = JsonSerializer.Serialize(new RuntimeClassifierConfig { TargetModel = targetModel }, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(Path.GetFullPath(_runtimeConfigPath));
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = _runtimeConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _runtimeConfigPath, overwrite: true);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class RuntimeClassifierConfig
    {
        public string? TargetModel { get; init; }
    }
}
