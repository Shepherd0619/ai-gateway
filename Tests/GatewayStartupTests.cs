using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiGateway.Proxy;
using Xunit;

namespace AiGateway.Tests;

public sealed class GatewayStartupTests
{
    [Fact]
    public async Task RealApplicationIssuesAuthenticatesListsUsageAndRevokesGatewayKey()
    {
        var directory=Path.Combine(Path.GetTempPath(),"gateway-startup-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var process=new Process { StartInfo=new ProcessStartInfo("dotnet") { WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
        process.StartInfo.ArgumentList.Add(typeof(ProxyHandler).Assembly.Location);
        var settings=new Dictionary<string,string>
        {
            ["contentRoot"]=directory,["Kestrel:Endpoints:Http:Url"]="http://127.0.0.1:0",
            ["GatewayKeys:Enabled"]="true",["ConnectionStrings:GatewayKeys"]=$"Data Source={Path.Combine(directory,"keys.db")};Pooling=False",
            ["Admin:ApiKey"]="startup-admin",["Admin:RuntimeConfigPath"]=Path.Combine(directory,"mappings.json"),
            ["ModelDiscovery:Enabled"]="true",["ModelDiscovery:Models:0:Id"]="test-model",["ForwardedHeaders:Enabled"]="false"
        };
        foreach(var (key,value) in settings) { process.StartInfo.ArgumentList.Add("--"+key);process.StartInfo.ArgumentList.Add(value); }
        try
        {
            Assert.True(process.Start());
            var errors=process.StandardError.ReadToEndAsync();
            string? address=null;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while(await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                const string prefix="Now listening on: ";
                var at=line.IndexOf(prefix,StringComparison.Ordinal);
                if(at>=0) { address=line[(at+prefix.Length)..].Trim();break; }
            }
            Assert.False(address is null,process.HasExited?await errors:"No listening address reported.");
            using var client=new HttpClient(new HttpClientHandler { UseProxy=false }) { BaseAddress=new Uri(address!) };
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/v1/models")).StatusCode);
            using var issueRequest=new HttpRequestMessage(HttpMethod.Post,"/admin/gateway-keys") { Content=JsonContent.Create(new { name="startup-client" }) };
            issueRequest.Headers.Add("x-admin-key","startup-admin");
            using var issue=await client.SendAsync(issueRequest);Assert.Equal(HttpStatusCode.Created,issue.StatusCode);
            using var json=JsonDocument.Parse(await issue.Content.ReadAsStringAsync());
            var token=json.RootElement.GetProperty("apiKey").GetString()!;var id=json.RootElement.GetProperty("id").GetGuid();
            using var modelsRequest=new HttpRequestMessage(HttpMethod.Get,"/v1/models");modelsRequest.Headers.Add("x-api-key",token);
            using var models=await client.SendAsync(modelsRequest);Assert.Equal(HttpStatusCode.OK,models.StatusCode);
            Assert.Contains("test-model",await models.Content.ReadAsStringAsync());
            using var listRequest=new HttpRequestMessage(HttpMethod.Get,"/admin/gateway-keys");listRequest.Headers.Add("x-admin-key","startup-admin");
            using var list=await client.SendAsync(listRequest);
            using var listed=JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var row=Assert.Single(listed.RootElement.GetProperty("keys").EnumerateArray());
            Assert.Equal("127.0.0.1",row.GetProperty("lastUsedIp").GetString());
            Assert.EndsWith("Z",row.GetProperty("lastUsedAt").GetString());
            using var revoke=new HttpRequestMessage(HttpMethod.Post,$"/admin/gateway-keys/{id}/revoke");revoke.Headers.Add("x-admin-key","startup-admin");
            Assert.Equal(HttpStatusCode.NoContent,(await client.SendAsync(revoke)).StatusCode);
            using var denied=new HttpRequestMessage(HttpMethod.Get,"/v1/models");denied.Headers.Add("Authorization","Bearer "+token);
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(denied)).StatusCode);
        }
        finally
        {
            if(process.Id!=0 && !process.HasExited) { process.Kill(entireProcessTree:true);await process.WaitForExitAsync(); }
            Directory.Delete(directory,true);
        }
    }
}
