using AiGateway.Compliance;
using AiGateway.Configuration;
using AiGateway.Discovery;
using AiGateway.Health;
using AiGateway.Proxy;

var builder = WebApplication.CreateSlimBuilder(args);

// ── Bind configuration ──
builder.Services.Configure<ModelMappingOptions>(builder.Configuration.GetSection("ModelMapping"));
builder.Services.Configure<ComplianceLogOptions>(builder.Configuration.GetSection("ComplianceLog"));

// ── Upstream HTTP client ──
var upstreamBaseUrl = builder.Configuration.GetValue<string>("Upstream:BaseUrl")
    ?? "https://openrouter.ai/api";

builder.Services.AddHttpClient("openrouter", client =>
{
    client.BaseAddress = new Uri(upstreamBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(10);
});

// ── Application services ──
builder.Services.AddSingleton<ModelMapper>();
builder.Services.AddSingleton<ComplianceLogWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ComplianceLogWriter>());
builder.Services.AddTransient<ProxyHandler>();

var app = builder.Build();

// ── Startup diagnostic ──
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var mapping = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelMappingOptions>>().Value;
startupLogger.LogInformation("Startup: upstream={Url}, rules=[{Rules}]",
    upstreamBaseUrl, string.Join(", ", mapping.Rules.Select(r => $"{r.Prefix}→{r.Target}")));

// ── Endpoints ──
app.MapHealthEndpoints();
app.MapModelDiscoveryEndpoints();
app.Map("/v1/{**catchAll}", app.Services.GetRequiredService<ProxyHandler>().Invoke);

app.Run();
