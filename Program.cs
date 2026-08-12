using AiGateway.Compliance;
using AiGateway.Configuration;
using AiGateway.Discovery;
using AiGateway.Health;
using AiGateway.Proxy;

var builder = WebApplication.CreateSlimBuilder(args);

// ── Bind configuration ──
builder.Services.Configure<ModelMappingOptions>(builder.Configuration.GetSection("ModelMapping"));
builder.Services.Configure<ComplianceLogOptions>(builder.Configuration.GetSection("ComplianceLog"));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection("Cors"));

// ── CORS ──
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("browser", policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod());
});

// ── Upstream HTTP client ──
var upstreamBaseUrl = builder.Configuration.GetValue<string>("Upstream:BaseUrl")
    ?? "https://openrouter.ai/api";

builder.Services.AddHttpClient("openrouter", client =>
{
    client.BaseAddress = new Uri(upstreamBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(10);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Recycle connections every 5 minutes to pick up DNS changes and
    // prevent stale connections from accumulating in the pool.
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    // Keep idle connections alive for 2 minutes to reduce TCP handshake
    // overhead for bursty traffic patterns.
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    // Enable multiple HTTP/2 connections to the same endpoint for
    // concurrent streaming requests.
    EnableMultipleHttp2Connections = true
});

// ── Proxy server HTTP clients ──
builder.Services.Configure<ProxyServerOptions>(builder.Configuration.GetSection("ProxyServers"));
var proxyServers = builder.Configuration.GetSection("ProxyServers").Get<ProxyServerOptions>();
if (proxyServers is not null)
{
    foreach (var (name, cfg) in proxyServers)
    {
        builder.Services.AddHttpClient($"openrouter-proxy-{name}", client =>
        {
            client.BaseAddress = new Uri(upstreamBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(10);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = new Socks5ConnectCallback(cfg.Address).Connect
        });
    }
}

// ── Validate ProxyServer references in mapping rules ──
var mappingRules = builder.Configuration.GetSection("ModelMapping:Rules").Get<List<MappingRule>>();
if (mappingRules is not null && proxyServers is not null)
{
    foreach (var rule in mappingRules)
    {
        if (!string.IsNullOrEmpty(rule.ProxyServer) && !proxyServers.ContainsKey(rule.ProxyServer))
        {
            var criticalMsg = $"Configuration error: MappingRule '{rule.Prefix}' references ProxyServer '{rule.ProxyServer}' which is not defined in ProxyServers section.";
            Console.Error.WriteLine(criticalMsg);
            throw new InvalidOperationException(criticalMsg);
        }
    }
}

// ── Application services ──
builder.Services.AddSingleton<ModelMapper>();
builder.Services.AddSingleton<ComplianceLogWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ComplianceLogWriter>());
builder.Services.AddTransient<ProxyHandler>();

var app = builder.Build();

app.UseCors("browser");

// ── Startup diagnostic ──
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var mapping = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelMappingOptions>>().Value;
var corsOpts = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorsOptions>>().Value;
startupLogger.LogInformation("Startup: upstream={Url}, origins=[{Origins}], rules=[{Rules}]",
    upstreamBaseUrl, string.Join(", ", corsOpts.AllowedOrigins),
    string.Join(", ", mapping.Rules.Select(r =>
        string.IsNullOrEmpty(r.ProxyServer)
            ? $"{r.Prefix}→{r.Target}"
            : $"{r.Prefix}→[proxy:{r.ProxyServer}]")));

// ── Endpoints ──
app.MapHealthEndpoints();
app.MapModelDiscoveryEndpoints();
app.Map("/v1/{**catchAll}", app.Services.GetRequiredService<ProxyHandler>().Invoke);

app.Run();
