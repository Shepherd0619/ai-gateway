using AiGateway.Admin;
using AiGateway.Compliance;
using AiGateway.Configuration;
using AiGateway.Discovery;
using AiGateway.Health;
using AiGateway.Proxy;

var builder = WebApplication.CreateSlimBuilder(args);

// ── Bind configuration ──
builder.Services.Configure<ModelMappingOptions>(builder.Configuration.GetSection("ModelMapping"));
builder.Services.Configure<ClassifierOptions>(builder.Configuration.GetSection("Classifier"));
builder.Services.Configure<ComplianceLogOptions>(builder.Configuration.GetSection("ComplianceLog"));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection("Cors"));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

// ── CORS ──
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("browser", policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod());
});

// ── Backend configuration and HTTP clients ──
var configuredBackends = builder.Configuration.GetSection("Backends").Get<BackendOptions>() ?? new BackendOptions();
var legacyBaseUrl = builder.Configuration.GetValue<string>("Upstream:BaseUrl") ?? "https://openrouter.ai/api";
var backends = BackendConfiguration.Normalize(configuredBackends, legacyBaseUrl);
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptions<BackendOptions>>(
    Microsoft.Extensions.Options.Options.Create(backends));

builder.Services.Configure<ProxyServerOptions>(builder.Configuration.GetSection("ProxyServers"));
var proxyServers = builder.Configuration.GetSection("ProxyServers").Get<ProxyServerOptions>() ?? new ProxyServerOptions();
var mappingRules = builder.Configuration.GetSection("ModelMapping:Rules").Get<List<MappingRule>>() ?? [];
BackendConfiguration.ValidateRules(mappingRules, backends);

foreach (var (backendName, backendConfig) in backends)
{
    builder.Services.AddHttpClient($"backend-{backendName}", client =>
    {
        client.BaseAddress = new Uri(backendConfig.BaseUrl);
        client.Timeout = TimeSpan.FromMinutes(10);
    }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true
    });

    foreach (var (proxyName, proxyConfig) in proxyServers)
    {
        builder.Services.AddHttpClient($"backend-{backendName}-proxy-{proxyName}", client =>
        {
            client.BaseAddress = new Uri(backendConfig.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(10);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = new Socks5ConnectCallback(proxyConfig.Address).Connect,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true
        });
    }
}

// ── Validate ProxyServer references in mapping rules ──
foreach (var rule in mappingRules)
{
    if (!string.IsNullOrEmpty(rule.ProxyServer) && !proxyServers.ContainsKey(rule.ProxyServer))
    {
        var criticalMsg = $"Configuration error: MappingRule '{rule.Prefix}' references ProxyServer '{rule.ProxyServer}' which is not defined in ProxyServers section.";
        Console.Error.WriteLine(criticalMsg);
        throw new InvalidOperationException(criticalMsg);
    }
}

var upstreamBaseUrl = backends[BackendOptions.DefaultBackendName].BaseUrl;

// ── Application services ──

// ── Application services ──
builder.Services.AddSingleton<RuntimeMappingStore>();
builder.Services.AddSingleton<RuntimeClassifierStore>();
builder.Services.AddSingleton<ModelMapper>();
builder.Services.AddSingleton<ComplianceLogWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ComplianceLogWriter>());
builder.Services.AddTransient<ProxyHandler>();

var app = builder.Build();

app.UseCors("browser");

// ── Startup diagnostic ──
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var mapping = app.Services.GetRequiredService<RuntimeMappingStore>();
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
app.MapAdminEndpoints();
app.Map("/v1/{**catchAll}", app.Services.GetRequiredService<ProxyHandler>().Invoke);

app.Run();
