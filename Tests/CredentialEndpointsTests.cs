using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiGateway.Admin;
using AiGateway.Authentication;
using AiGateway.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class CredentialEndpointsTests
{
    [Fact]
    public async Task CredentialsRequireAdminAndExposeOnlyStatusWithMemoryOverrides()
    {
        await using var host = await AdminHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(HttpMethod.Get,"/admin/backend-credentials",authorize:false)).StatusCode);
        using var list = await host.Send(HttpMethod.Get,"/admin/backend-credentials");
        Assert.Equal(HttpStatusCode.OK,list.StatusCode);
        Assert.DoesNotContain("provider-secret",await list.Content.ReadAsStringAsync());
        using var put = await host.Send(HttpMethod.Put,"/admin/backend-credentials/OPENROUTER",new { apiKey="temporary-secret" });
        Assert.Equal(HttpStatusCode.OK,put.StatusCode);
        Assert.True(put.Headers.CacheControl!.NoStore);
        Assert.Equal("temporary-secret",host.Credentials.GetKey("openrouter"));
        Assert.False(Directory.Exists(host.RuntimeDirectory));
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Send(HttpMethod.Put,"/admin/backend-credentials/openrouter",new { apiKey="sk-aigw-invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await host.Send(HttpMethod.Put,"/admin/backend-credentials/missing",new { apiKey="secret" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await host.Send(HttpMethod.Delete,"/admin/backend-credentials/openrouter")).StatusCode);
        Assert.Equal("provider-secret",host.Credentials.GetKey("openrouter"));
        Assert.Equal(HttpStatusCode.OK,(await host.Send(HttpMethod.Put,"/admin/backend-credentials?name=custom%2Fname",new { apiKey="custom-secret" })).StatusCode);
        Assert.Equal("custom-secret",host.Credentials.GetKey("custom/name"));
        Assert.Equal(HttpStatusCode.OK,(await host.Send(HttpMethod.Delete,"/admin/backend-credentials?name=custom%2Fname")).StatusCode);
    }

    [Fact]
    public async Task GatewayKeyLifecycleReturnsSecretOnlyOnIssueAndGuardsAdmin()
    {
        await using var host = await AdminHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,(await host.Send(HttpMethod.Post,"/admin/gateway-keys",new { name="client" },false)).StatusCode);
        using var issued = await host.Send(HttpMethod.Post,"/admin/gateway-keys",new { name="client",expiresAt=(string?)null });
        Assert.Equal(HttpStatusCode.Created,issued.StatusCode);
        Assert.True(issued.Headers.CacheControl!.NoStore);
        using var json=JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var token=json.RootElement.GetProperty("apiKey").GetString()!;
        var id=json.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("lastUsedIp").ValueKind);
        Assert.NotNull(await host.Db.Store.AuthenticateAsync(token));
        using var list=await host.Send(HttpMethod.Get,"/admin/gateway-keys?limit=1&offset=0");
        var body=await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token,body);Assert.DoesNotContain("apiKey",body);Assert.DoesNotContain("tokenHash",body);
        Assert.Equal(HttpStatusCode.NoContent,(await host.Send(HttpMethod.Post,$"/admin/gateway-keys/{id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await host.Send(HttpMethod.Post,$"/admin/gateway-keys/{id}/revoke")).StatusCode);
        Assert.Null(await host.Db.Store.AuthenticateAsync(token));
        Assert.Equal(HttpStatusCode.NotFound,(await host.Send(HttpMethod.Post,$"/admin/gateway-keys/{Guid.NewGuid()}/revoke")).StatusCode);
    }

    [Theory]
    [InlineData("/admin/gateway-keys?limit=0")]
    [InlineData("/admin/gateway-keys?offset=-1")]
    [InlineData("/admin/gateway-keys?limit=invalid")]
    public async Task InvalidPaginationIs400(string url)
    {
        await using var host=await AdminHost.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Send(HttpMethod.Get,url)).StatusCode);
    }

    [Fact]
    public async Task DatabaseOutageReturns503ButMemoryCredentialAdminStillWorks()
    {
        await using var host=await AdminHost.StartAsync();
        await using(var db=host.Db.Factory.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TABLE gateway_api_keys");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await host.Send(HttpMethod.Post,"/admin/gateway-keys",new { name="client" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await host.Send(HttpMethod.Get,"/admin/gateway-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await host.Send(HttpMethod.Get,"/admin/backend-credentials")).StatusCode);
    }

    [Fact]
    public async Task DisabledKeyFeatureOrAdminReturns404()
    {
        await using(var host=await AdminHost.StartAsync(enabled:false))
        {
            Assert.Equal(HttpStatusCode.NotFound,(await host.Send(HttpMethod.Get,"/admin/gateway-keys")).StatusCode);
            Assert.Equal(HttpStatusCode.OK,(await host.Send(HttpMethod.Get,"/admin/backend-credentials")).StatusCode);
        }
        await using(var host=await AdminHost.StartAsync(admin:false))
            Assert.Equal(HttpStatusCode.NotFound,(await host.Send(HttpMethod.Get,"/admin/backend-credentials")).StatusCode);
    }

    internal sealed class AdminHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }
        public KeyTestDatabase Db { get; }
        public BackendCredentialStore Credentials => _app.Services.GetRequiredService<BackendCredentialStore>();
        public string RuntimeDirectory { get; }
        private AdminHost(WebApplication app,HttpClient client,KeyTestDatabase db,string dir) { _app=app;Client=client;Db=db;RuntimeDirectory=dir; }
        public static async Task<AdminHost> StartAsync(bool enabled=true,bool admin=true)
        {
            var db=await KeyTestDatabase.CreateAsync();
            var dir=Path.Combine(Path.GetTempPath(),"gateway-admin-"+Guid.NewGuid().ToString("N"));
            var builder=WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath=Path.GetTempPath() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o=>o.Listen(IPAddress.Loopback,0));
            var services=builder.Services;
            services.AddSingleton<IOptions<BackendOptions>>(Options.Create(new BackendOptions { ["openrouter"]=new() { BaseUrl="https://upstream.example",ApiKey="provider-secret" },["custom/name"]=new() { BaseUrl="https://other.example" } }));
            services.AddSingleton<IOptions<AdminOptions>>(Options.Create(new AdminOptions { ApiKey=admin?"admin-test":null,RuntimeConfigPath=Path.Combine(dir,"mappings.json") }));
            services.Configure<ModelMappingOptions>(_=>{});services.Configure<ClassifierOptions>(_=>{});services.Configure<ModelDiscoveryOptions>(_=>{});services.Configure<ProxyServerOptions>(_=>{});
            services.AddSingleton<IOptions<GatewayKeyOptions>>(Options.Create(new GatewayKeyOptions { Enabled=enabled }));
            services.AddSingleton(db.Store);services.AddSingleton<BackendCredentialStore>();
            services.AddSingleton<RuntimeMappingStore>();services.AddSingleton<RuntimeClassifierStore>();services.AddSingleton<RuntimeModelDiscoveryStore>();services.AddSingleton<RuntimeBackendTranslationStore>();services.AddHttpClient();
            var app=builder.Build();app.MapAdminEndpoints();await app.StartAsync();
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client=new HttpClient(new HttpClientHandler { UseProxy=false }) { BaseAddress=new Uri(address) };
            return new(app,client,db,dir);
        }
        public Task<HttpResponseMessage> Send(HttpMethod method,string path,object? body=null,bool authorize=true)
        {
            var request=new HttpRequestMessage(method,path);
            if(body is not null) request.Content=JsonContent.Create(body);
            if(authorize)request.Headers.Add("x-admin-key","admin-test");
            return Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { Client.Dispose();await _app.DisposeAsync();await Db.DisposeAsync();if(Directory.Exists(RuntimeDirectory))Directory.Delete(RuntimeDirectory,true); }
    }
}
