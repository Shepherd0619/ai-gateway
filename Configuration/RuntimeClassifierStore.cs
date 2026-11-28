using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiGateway.Configuration;

internal sealed record ClassifierSnapshot(
    string? Target,
    string? Backend,
    string? ProxyServer,
    string Source)
{
    private IReadOnlyList<string>? _proxyServers;

    public IReadOnlyList<string> ProxyServers
    {
        get => _proxyServers ?? (ProxyServer is null ? [] : [ProxyServer]);
        init => _proxyServers = value;
    }

    public bool Enabled => !string.IsNullOrEmpty(Target);
}
internal sealed class RuntimeClassifierStore
{
    private readonly ClassifierOptions _baseOptions;
    private readonly string _runtimeConfigPath;
    private readonly BackendOptions _backends;
    private readonly ProxyServerOptions _proxyServers;
    private readonly ILogger<RuntimeClassifierStore> _logger;
    private volatile ClassifierSnapshot _snapshot;

    public ClassifierSnapshot Snapshot => _snapshot;

    public RuntimeClassifierStore(
        IOptions<ClassifierOptions> baseOptions,
        IOptions<AdminOptions> adminOptions,
        IOptions<BackendOptions> backendOptions,
        IOptions<ProxyServerOptions> proxyOptions,
        ILogger<RuntimeClassifierStore> logger)
    {
        _baseOptions = Normalize(baseOptions.Value);
        _runtimeConfigPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(adminOptions.Value.RuntimeConfigPath)) ?? ".",
            "classifier-runtime.json");
        _backends = backendOptions.Value;
        _proxyServers = proxyOptions.Value;
        _logger = logger;
        Validate(_baseOptions);
        _snapshot = LoadSnapshot();
    }

    public ClassifierSnapshot Save(ClassifierOptions options)
    {
        var normalized = Normalize(options);
        Validate(normalized);
        WriteRuntimeFile(normalized);
        _snapshot = ToSnapshot(normalized, "runtime");
        _logger.LogInformation("Runtime classifier saved: {Target} to {Path}", normalized.Target, _runtimeConfigPath);
        return _snapshot;
    }

    public ClassifierSnapshot Delete()
    {
        if (File.Exists(_runtimeConfigPath))
            File.Delete(_runtimeConfigPath);

        _snapshot = ToSnapshot(_baseOptions, "base");
        _logger.LogInformation("Runtime classifier deleted; reverted to base configuration");
        return _snapshot;
    }

    private ClassifierSnapshot LoadSnapshot()
    {
        if (!File.Exists(_runtimeConfigPath))
            return ToSnapshot(_baseOptions, "base");

        try
        {
            var json = File.ReadAllText(_runtimeConfigPath);
            var runtime = JsonSerializer.Deserialize<ClassifierOptions>(json) ?? new ClassifierOptions();
            var normalized = Normalize(runtime);
            Validate(normalized);
            return ToSnapshot(normalized, "runtime");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load classifier runtime config from {Path}, using base configuration", _runtimeConfigPath);
            return ToSnapshot(_baseOptions, "base");
        }
    }

    private void WriteRuntimeFile(ClassifierOptions options)
    {
        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(Path.GetFullPath(_runtimeConfigPath));
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = _runtimeConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _runtimeConfigPath, overwrite: true);
    }

    private void Validate(ClassifierOptions options)
    {
        foreach (var proxyServer in options.ProxyServers ?? [])
        {
            if (string.IsNullOrWhiteSpace(proxyServer) || !_proxyServers.ContainsKey(proxyServer))
            {
                throw new InvalidOperationException(
                    $"ProxyServer '{proxyServer}' referenced by classifier is not defined in ProxyServers section.");
            }
        }

        if (string.IsNullOrEmpty(options.Target))
            return;

        var backendName = string.IsNullOrEmpty(options.Backend)
            ? BackendOptions.DefaultBackendName
            : options.Backend;
        if (!_backends.ContainsKey(backendName))
        {
            throw new InvalidOperationException(
                $"Backend '{backendName}' referenced by classifier is not defined in Backends section.");
        }
    }
    private static ClassifierOptions Normalize(ClassifierOptions options)
    {
        var proxyServers = options.ProxyServers;
        if (proxyServers is null)
            proxyServers = string.IsNullOrWhiteSpace(options.ProxyServer) ? [] : [options.ProxyServer.Trim()];

        if (proxyServers is not null)
            proxyServers = proxyServers.Select(proxy => proxy?.Trim() ?? "")
                .Distinct(StringComparer.Ordinal)
                .ToList();

        return options with
        {
            Target = string.IsNullOrWhiteSpace(options.Target) ? "" : options.Target.Trim(),
            Backend = string.IsNullOrWhiteSpace(options.Backend) ? null : options.Backend.Trim(),
            ProxyServers = proxyServers,
            ProxyServer = null,
        };
    }

    private static ClassifierSnapshot ToSnapshot(ClassifierOptions options, string source) =>
        new(options.Target, options.Backend ?? BackendOptions.DefaultBackendName, options.ProxyServers?.FirstOrDefault(), source)
        {
            ProxyServers = options.ProxyServers ?? []
        };
}
