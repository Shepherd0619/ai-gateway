using AiGateway.Configuration;

namespace AiGateway.Proxy;

internal sealed record BackendRoute(string Url, string HttpClientName)
{
    internal static BackendRoute Create(
        MapResult mapResult,
        BackendOptions backends,
        string path) =>
        Create(mapResult, backends, path, mapResult.ProxyServers.FirstOrDefault());

    internal static BackendRoute Create(
        MapResult mapResult,
        BackendOptions backends,
        string path,
        string? proxyServer)
    {
        var backendName = string.IsNullOrEmpty(mapResult.Backend)
            ? BackendOptions.DefaultBackendName
            : mapResult.Backend;
        var backend = backends[backendName];
        var clientName = string.IsNullOrEmpty(proxyServer)
            ? $"backend-{backendName}"
            : $"backend-{backendName}-proxy-{proxyServer}";

        return new BackendRoute(backend.BaseUrl.TrimEnd('/') + path, clientName);
    }
}
