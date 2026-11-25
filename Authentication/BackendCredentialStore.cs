using AiGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AiGateway.Authentication;

internal sealed record BackendCredentialView(string Name, bool Configured, string Source);

internal sealed class BackendCredentialStore
{
    private readonly Dictionary<string, string?> _configured;
    private readonly object _writeLock = new();
    private volatile Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public BackendCredentialStore(IOptions<BackendOptions> options)
    {
        _configured = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, backend) in options.Value)
        {
            if (!string.IsNullOrWhiteSpace(backend.ApiKey)) ValidateKey(backend.ApiKey);
            _configured[name] = string.IsNullOrWhiteSpace(backend.ApiKey) ? null : backend.ApiKey;
        }
    }

    public IReadOnlyList<BackendCredentialView> All
    {
        get
        {
            var snapshot = _overrides;
            return _configured.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(name => View(name, snapshot)).ToArray();
        }
    }

    public string? GetKey(string name)
    {
        var snapshot = _overrides;
        return snapshot.TryGetValue(name, out var key) ? key : _configured[name];
    }

    public BackendCredentialView Save(string name, string key)
    {
        var canonical = Canonical(name);
        ValidateKey(key);
        lock (_writeLock)
        {
            var next = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase) { [canonical] = key };
            _overrides = next;
            return View(canonical, next);
        }
    }

    public BackendCredentialView Delete(string name)
    {
        var canonical = Canonical(name);
        lock (_writeLock)
        {
            var next = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase);
            next.Remove(canonical);
            _overrides = next;
            return View(canonical, next);
        }
    }

    private string Canonical(string name) => _configured.Keys.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException("Backend is not configured.");

    private BackendCredentialView View(string name, Dictionary<string, string> snapshot) =>
        snapshot.ContainsKey(name) ? new(name, true, "runtime") : new(name, _configured[name] is not null, _configured[name] is null ? "none" : "configuration");

    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            key.StartsWith("sk-aigw-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provider key must be a nonempty credential without whitespace/control characters or the gateway key prefix.");
    }
}
