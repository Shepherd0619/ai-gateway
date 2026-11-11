using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace AiGateway.Authentication;

internal static class TrustedProxyConfiguration
{
    public static IApplicationBuilder UseTrustedForwarding(this IApplicationBuilder app, IConfiguration config)
    {
        if (!config.GetValue<bool>("ForwardedHeaders:Enabled")) return app;
        var proxies = config.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        if (proxies.Length == 0) throw new InvalidOperationException("ForwardedHeaders requires an explicit KnownProxies list.");
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var value in proxies)
        {
            if (!IPAddress.TryParse(value, out var ip)) throw new InvalidOperationException("ForwardedHeaders KnownProxies contains an invalid IP address.");
            options.KnownProxies.Add(ip);
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) options.KnownProxies.Add(ip.MapToIPv6());
        }
        return app.UseForwardedHeaders(options);
    }
}
