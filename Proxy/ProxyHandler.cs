using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Compliance;

namespace AiGateway.Proxy;

internal sealed class ProxyHandler
{
    private readonly IHttpClientFactory _hcf;
    private readonly ILogger<ProxyHandler> _logger;
    private readonly ModelMapper _mapper;
    private readonly ComplianceLogWriter _complianceWriter;
    private readonly string _upstreamBaseUrl;

    public ProxyHandler(
        IHttpClientFactory hcf,
        ILogger<ProxyHandler> logger,
        ModelMapper mapper,
        ComplianceLogWriter complianceWriter,
        IConfiguration configuration)
    {
        _hcf = hcf;
        _logger = logger;
        _mapper = mapper;
        _complianceWriter = complianceWriter;
        _upstreamBaseUrl = configuration.GetValue<string>("Upstream:BaseUrl")
            ?? "https://openrouter.ai/api";
    }

    internal async Task Invoke(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";

        // 1. Extract API key
        var clientKey = ctx.Request.Headers["x-api-key"].FirstOrDefault();
        if (string.IsNullOrEmpty(clientKey))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"error":{"type":"authentication_error","message":"Missing x-api-key"}}""", ctx.RequestAborted);
            return;
        }

        // 2. Read request body
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ctx.RequestAborted);

        // 3. Rewrite model + handle streaming preference
        string? originalModel = null;
        string? upstreamModel = null;
        var bodyToSend = body;
        var contentType = ctx.Request.ContentType ?? "application/json";
        bool clientWantsStream = false;

        if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith('{'))
        {
            try
            {
                var json = JsonNode.Parse(body);
                if (json is not null)
                {
                    // Detect client streaming preference before we force it
                    if (json["stream"] is JsonValue streamNode)
                        clientWantsStream = streamNode.GetValueKind() == JsonValueKind.True;

                    // Force non-streaming only if the client didn't request it
                    if (!clientWantsStream)
                        json["stream"] = false;

                    if (json["model"] is JsonValue modelNode && modelNode.TryGetValue(out string? model))
                    {
                        originalModel = model;
                        upstreamModel = _mapper.Map(originalModel);
                        if (upstreamModel != originalModel)
                        {
                            json["model"] = upstreamModel;
                        }
                    }
                    bodyToSend = json.ToJsonString();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Body isn't valid JSON — forward as-is
            }
        }

        // 4. Build and send upstream request
        var fullUrl = _upstreamBaseUrl + path;
        var client = _hcf.CreateClient("openrouter");
        using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), fullUrl)
        {
            Content = new StringContent(bodyToSend, Encoding.UTF8, contentType)
        };
        upstreamReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {clientKey}");

        var anthropicVersion = ctx.Request.Headers["anthropic-version"].FirstOrDefault() ?? "2023-06-01";
        upstreamReq.Headers.TryAddWithoutValidation("anthropic-version", anthropicVersion);

        _logger.LogDebug("DIAG upstream req {Method} {FullUrl} originalModel={Orig} upstreamModel={Up} bodyLen={BodyLen} body={Body}",
            ctx.Request.Method, fullUrl, originalModel, upstreamModel, bodyToSend.Length,
            bodyToSend.Length > 500 ? bodyToSend[..500] : bodyToSend);

        var sw = Stopwatch.StartNew();
        HttpResponseMessage upstreamResp;
        try
        {
            var completionOption = clientWantsStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
            upstreamResp = await client.SendAsync(upstreamReq, completionOption, ctx.RequestAborted);
        }
        catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = 504;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"error":{"type":"timeout","message":"Upstream request timed out"}}""", ctx.RequestAborted);
            return;
        }
        sw.Stop();

        // 5. Handle response — streaming or non-streaming
        if (clientWantsStream && upstreamResp.IsSuccessStatusCode)
        {
            await StreamSseResponse(ctx, upstreamResp, originalModel, upstreamModel, body, path, sw);
        }
        else
        {
            // 5a. Read upstream response (non-streaming or error)
            var respBody = await upstreamResp.Content.ReadAsStringAsync(ctx.RequestAborted);

            // ── Diagnostic logging ──
            _logger.LogDebug("DIAG upstream status={Status} content-type={ContentType} bodyLen={BodyLen} body={Body}",
                (int)upstreamResp.StatusCode, upstreamResp.Content.Headers.ContentType, respBody.Length,
                respBody.Length > 500 ? respBody[..500] : respBody);

            // 6. Rewrite response model back (e.g. DeepSeek → original Claude name)
            if (originalModel is not null && upstreamModel is not null && upstreamModel != originalModel)
            {
                try
                {
                    var respJson = JsonNode.Parse(respBody);
                    if (respJson?["model"] is not null)
                    {
                        respJson["model"] = originalModel;
                        respBody = respJson.ToJsonString();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Response not valid JSON — forward as-is
                }
            }

            // 7. Compliance log (non-blocking via Channel)
            _complianceWriter.TryWrite(new ComplianceEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Path = path,
                Method = ctx.Request.Method,
                ClientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
                OriginalModel = originalModel,
                UpstreamModel = upstreamModel,
                StatusCode = (int)upstreamResp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                RequestBody = body,
                ResponseBody = respBody
            });

            // 8. Return proxied response
            ctx.Response.StatusCode = (int)upstreamResp.StatusCode;

            foreach (var h in upstreamResp.Headers)
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
            foreach (var h in upstreamResp.Content.Headers)
                ctx.Response.Headers.TryAdd(h.Key, h.Value.ToArray());

            // Remove transfer-encoding and content-length — body length changes after model rewrite
            ctx.Response.Headers.Remove("transfer-encoding");
            ctx.Response.Headers.Remove("content-length");

            ctx.Response.ContentType = upstreamResp.Content.Headers.ContentType?.ToString() ?? "application/json";

            _logger.LogDebug("DIAG response content-type={ContentType} bodyLen={BodyLen} body={Body}",
                ctx.Response.ContentType, respBody.Length,
                respBody.Length > 500 ? respBody[..500] : respBody);

            await ctx.Response.WriteAsync(respBody, ctx.RequestAborted);

            _logger.LogInformation("Proxy {Method} {Path} {Original}->{Upstream} {StatusCode} {DurationMs}ms",
                ctx.Request.Method, path, originalModel ?? "-", upstreamModel ?? "-",
                (int)upstreamResp.StatusCode, sw.ElapsedMilliseconds);
        }
    }

    private async Task StreamSseResponse(
        HttpContext ctx,
        HttpResponseMessage upstreamResp,
        string? originalModel,
        string? upstreamModel,
        string requestBody,
        string path,
        Stopwatch sw)
    {
        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["cache-control"] = "no-cache, no-store";
        ctx.Response.Headers["connection"] = "keep-alive";

        using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ctx.RequestAborted);
        using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
        var doRewrite = originalModel is not null && upstreamModel is not null && upstreamModel != originalModel;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ctx.RequestAborted);

            if (line is null)
                break;

            // Rewrite model name in data: lines
            if (doRewrite && line.StartsWith("data:") && line.Contains(upstreamModel!))
            {
                line = line.Replace(upstreamModel!, originalModel!);
            }

            await ctx.Response.WriteAsync(line + "\n", ctx.RequestAborted);

            // Flush after blank lines (SSE event boundaries) for low-latency delivery
            if (line.Length == 0)
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }

        // Compliance log (abbreviated — we can't capture the full SSE stream)
        _complianceWriter.TryWrite(new ComplianceEntry
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            Path = path,
            Method = ctx.Request.Method,
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
            OriginalModel = originalModel,
            UpstreamModel = upstreamModel,
            StatusCode = (int)upstreamResp.StatusCode,
            DurationMs = sw.ElapsedMilliseconds,
            RequestBody = requestBody,
            ResponseBody = "[streaming]"
        });

        _logger.LogInformation("Proxy SSE {Method} {Path} {Original}->{Upstream} {StatusCode} {DurationMs}ms",
            ctx.Request.Method, path, originalModel ?? "-", upstreamModel ?? "-",
            (int)upstreamResp.StatusCode, sw.ElapsedMilliseconds);
    }
}
