using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiGateway.Configuration;

internal sealed class RuntimeMappingStore
{
    private readonly IReadOnlyList<MappingRule> _baseRules;
    private readonly string _runtimeConfigPath;
    private readonly ProxyServerOptions _proxyServers;
    private readonly ILogger<RuntimeMappingStore> _logger;
    private volatile IReadOnlyList<MappingRule> _rules;

    /// <summary>
    /// Thread-safe merged view of current rules.
    /// Read by the proxy on every request; written by the admin API on save.
    /// </summary>
    public IReadOnlyList<MappingRule> Rules => _rules;

    public RuntimeMappingStore(
        IOptions<ModelMappingOptions> baseOptions,
        IOptions<AdminOptions> adminOptions,
        IOptions<ProxyServerOptions> proxyOptions,
        ILogger<RuntimeMappingStore> logger)
    {
        _baseRules = baseOptions.Value.Rules;
        _runtimeConfigPath = adminOptions.Value.RuntimeConfigPath;
        _proxyServers = proxyOptions.Value;
        _logger = logger;
        _rules = LoadMerged();
    }

    /// <summary>
    /// Replace all runtime rules. Writes to file, then updates in-memory merged view.
    /// </summary>
    public void Save(List<MappingRule> runtimeRules)
    {
        ValidateProxyServers(runtimeRules);
        WriteRuntimeFile(runtimeRules);
        _rules = Merge(runtimeRules);
        _logger.LogInformation("Runtime mappings saved: {Count} rules written to {Path}",
            runtimeRules.Count, _runtimeConfigPath);
    }

    /// <summary>
    /// Insert or update a single rule by prefix.
    /// </summary>
    public void Upsert(MappingRule rule)
    {
        ValidateProxyServers(new[] { rule });
        var runtimeRules = LoadRuntimeRules();

        var idx = runtimeRules.FindIndex(r =>
            r.Prefix.Equals(rule.Prefix, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            runtimeRules[idx] = rule;
        else
            runtimeRules.Add(rule);

        WriteRuntimeFile(runtimeRules);
        _rules = Merge(runtimeRules);
        _logger.LogInformation("Runtime mapping upserted: {Prefix} -> {Target}", rule.Prefix, rule.Target);
    }

    /// <summary>
    /// Remove a runtime override by prefix. Falls back to the base rule.
    /// </summary>
    public void Delete(string prefix)
    {
        var runtimeRules = LoadRuntimeRules();
        runtimeRules.RemoveAll(r =>
            r.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));

        WriteRuntimeFile(runtimeRules);
        _rules = Merge(runtimeRules);
        _logger.LogInformation("Runtime mapping deleted: {Prefix}", prefix);
    }

    // ── private helpers ──

    private List<MappingRule> LoadMerged()
    {
        return Merge(LoadRuntimeRules());
    }

    private List<MappingRule> Merge(List<MappingRule> runtimeRules)
    {
        var merged = new Dictionary<string, MappingRule>(StringComparer.OrdinalIgnoreCase);

        // Start with base rules (appsettings.json + env var overrides)
        foreach (var rule in _baseRules)
            merged[rule.Prefix] = rule;

        // Overlay runtime rules by matching Prefix
        foreach (var rule in runtimeRules)
            merged[rule.Prefix] = rule;

        return merged.Values.ToList();
    }

    private List<MappingRule> LoadRuntimeRules()
    {
        if (!File.Exists(_runtimeConfigPath))
            return [];

        try
        {
            var json = File.ReadAllText(_runtimeConfigPath);
            return JsonSerializer.Deserialize<List<MappingRule>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load runtime config from {Path}, ignoring runtime overrides",
                _runtimeConfigPath);
            return [];
        }
    }

    private void WriteRuntimeFile(List<MappingRule> rules)
    {
        var json = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(Path.GetFullPath(_runtimeConfigPath));
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_runtimeConfigPath, json);
    }

    private void ValidateProxyServers(IEnumerable<MappingRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!string.IsNullOrEmpty(rule.ProxyServer) && !_proxyServers.ContainsKey(rule.ProxyServer))
            {
                throw new InvalidOperationException(
                    $"ProxyServer '{rule.ProxyServer}' referenced by rule '{rule.Prefix}' is not defined in ProxyServers section.");
            }
        }
    }
}