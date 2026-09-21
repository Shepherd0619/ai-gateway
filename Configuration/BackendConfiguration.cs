namespace AiGateway.Configuration;

internal static class BackendConfiguration
{
    internal static BackendOptions Normalize(BackendOptions configured, string? legacyBaseUrl)
    {
        var normalized = new BackendOptions();
        foreach (var (name, config) in configured)
            normalized[name] = config;

        if (!normalized.ContainsKey(BackendOptions.DefaultBackendName))
        {
            if (string.IsNullOrWhiteSpace(legacyBaseUrl))
                throw new InvalidOperationException("No openrouter backend is configured and Upstream:BaseUrl is empty.");

            normalized[BackendOptions.DefaultBackendName] = new BackendConfig { BaseUrl = legacyBaseUrl };
        }

        foreach (var (name, config) in normalized)
        {
            if (string.IsNullOrWhiteSpace(config.BaseUrl))
                throw new InvalidOperationException($"Backend '{name}' has an empty BaseUrl.");
        }

        return normalized;
    }

    internal static void ValidateRules(
        IEnumerable<MappingRule> rules,
        BackendOptions backends)
    {
        foreach (var rule in rules)
        {
            var backendName = string.IsNullOrEmpty(rule.Backend)
                ? BackendOptions.DefaultBackendName
                : rule.Backend;

            if (!backends.ContainsKey(backendName))
            {
                throw new InvalidOperationException(
                    $"Backend '{backendName}' referenced by rule '{rule.Prefix}' is not defined in Backends section.");
            }
        }
    }
}
