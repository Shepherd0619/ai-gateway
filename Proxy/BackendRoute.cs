using AiGateway.Configuration;

namespace AiGateway.Proxy;

internal sealed record BackendRoute(string Url, string HttpClientName)
{
    internal static BackendRoute Create(
        MapResult mapResult,
        BackendOptions backends,
        string path)
    {
        var backendName = string.IsNullOrEmpty(mapResult.Backend)
            ? BackendOptions.DefaultBackendName
            : mapResult.Backend;
        var backend = backends[backendName];
        var clientName = string.IsNullOrEmpty(mapResult.ProxyServer)
            ? $"backend-{backendName}"
            : $"backend-{backendName}-proxy-{mapResult.ProxyServer}";

        return new BackendRoute(backend.BaseUrl.TrimEnd('/') + path, clientName);
    }
}
