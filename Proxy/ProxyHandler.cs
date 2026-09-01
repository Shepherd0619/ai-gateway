using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiGateway.Compliance;
using AiGateway.Configuration;

namespace AiGateway.Proxy;

internal sealed class ProxyHandler
{
    private readonly IHttpClientFactory _hcf;
    private readonly ILogger<ProxyHandler> _logger;
    private readonly ModelMapper _mapper;
    private readonly ComplianceLogWriter _complianceWriter;
    private readonly string _upstreamBaseUrl;
    private readonly RuntimeClassifierStore _classifier;

    public ProxyHandler(
        IHttpClientFactory hcf,
        ILogger<ProxyHandler> logger,
        ModelMapper mapper,
        ComplianceLogWriter complianceWriter,
        RuntimeClassifierStore classifier,
        IConfiguration configuration)
    {
        _hcf = hcf;
        _logger = logger;
        _mapper = mapper;
        _complianceWriter = complianceWriter;
        _classifier = classifier;
        _upstreamBaseUrl = configuration.GetValue<string>("Upstream:BaseUrl")
            ?? "https://openrouter.ai/api";
    }

    internal static void ForwardClientIdentityHeaders(HttpRequest clientRequest, HttpRequestMessage upstreamRequest)
    {
        // Forward only headers that identify the downstream application/client.
        // Gateway-controlled authentication and protocol headers stay authoritative.
        string[] identityHeaders =
        [
            "User-Agent",
            "X-Title",
            "X-OpenRouter-Title",
            "HTTP-Referer",
            "Referer",
            "X-Client-Name"
        ];

        foreach (var headerName in identityHeaders)
        {
            if (!clientRequest.Headers.TryGetValue(headerName, out var values) ||
                upstreamRequest.Headers.Contains(headerName))
                continue;

            upstreamRequest.Headers.TryAddWithoutValidation(headerName, values.ToArray());
        }
    }

    internal async Task Invoke(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";

        // 1. Extract API key — supports both x-api-key (desktop) and Authorization: Bearer (CLI)
        var clientKey = ctx.Request.Headers["x-api-key"].FirstOrDefault();
        if (string.IsNullOrEmpty(clientKey))
        {
            var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                clientKey = authHeader["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrEmpty(clientKey))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"error":{"type":"authentication_error","message":"Missing x-api-key or Authorization: Bearer header"}}""", ctx.RequestAborted);
            return;
        }

        // 2. Read request body as raw UTF-8 bytes — a single buffered copy, no
        //    intermediate string.  (Previous approach read a UTF-16 string, parsed
        //    a JsonNode DOM, and re-serialized to another string, holding ~5-6 full
        //    copies in memory and OOM-ing under a tight container memory limit.)
        using var bodyStream = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(bodyStream, ctx.RequestAborted);
        var bodyBytes = bodyStream.ToArray();

        // 3. Rewrite model + detect streaming preference in a single zero-copy parse.
        string? originalModel = null;
        string? upstreamModel = null;
        string? role = null;
        string? proxyServer = null;
        byte[] bodyToSend = bodyBytes;
        var contentType = ctx.Request.ContentType ?? "application/json";
        bool clientWantsStream = false;
        bool stripToolChoice = false;

        if (bodyBytes.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(bodyBytes);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    // Detect client streaming preference from the top-level "stream" field.
                    // We no longer force stream:false — upstream defaults to non-streaming.
                    if (root.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind == JsonValueKind.True)
                        clientWantsStream = true;

                    // ── Classifier detection ──
                    // Auto-mode security classifier requests are short safety checks.
                    // Route them to a faster model (e.g. deepseek-v4-flash) so they
                    // stay responsive even when the main model is slow or unavailable.
                    var classifierSnapshot = _classifier.Snapshot;
                    var isClassifier = classifierSnapshot.Enabled && ClassifierDetector.IsClassifierRequest(root);

                    string? newModel = null;
                    if (isClassifier)
                    {
                        role = "classifier";
                        originalModel = GetModel(root) ?? "claude-sonnet-4-20250514";
                        var mapResult = _mapper.Map(classifierSnapshot.TargetModel!);
                        upstreamModel = mapResult.TargetModel;
                        proxyServer = mapResult.ProxyServer;
                        newModel = upstreamModel;
                        LogClassifierDiagnostics(root, isClassifier);
                    }
                    else if (GetModel(root) is { } model)
                    {
                        originalModel = model;
                        var mapResult = _mapper.Map(model);
                        upstreamModel = mapResult.TargetModel;
                        proxyServer = mapResult.ProxyServer;
                        if (upstreamModel != model)
                            newModel = upstreamModel;
                    }

                    stripToolChoice = HasWebSearchTool(root) && root.TryGetProperty("tool_choice", out _);

                    // Re-serialize only when we actually changed something (model rewrite,
                    // classifier system-strip, or the OpenRouter web-search workaround).
                    if (newModel is not null || stripToolChoice)
                    {
                        using var outStream = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(outStream))
                        {
                            WriteTransformed(writer, root, newModel, isClassifier, stripToolChoice);
                        }
                        bodyToSend = outStream.ToArray();
                    }
                }
            }
            catch (JsonException)
            {
                // Body isn't valid JSON — forward raw bytes as-is
            }
        }

        // Compliance log records the *original* client body.  Decode it only when
        // compliance logging is enabled (otherwise the bytes are never needed again).
        var requestBody = _complianceWriter.IsEnabled ? Encoding.UTF8.GetString(bodyBytes) : string.Empty;

        // 4. Build and send upstream request
        var fullUrl = _upstreamBaseUrl + path;
        var httpClientName = !string.IsNullOrEmpty(proxyServer)
            ? $"openrouter-proxy-{proxyServer}"
            : "openrouter";
        var client = _hcf.CreateClient(httpClientName);
        using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), fullUrl)
        {
            Content = new ByteArrayContent(bodyToSend)
        };
        upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        upstreamReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {clientKey}");

        var anthropicVersion = ctx.Request.Headers["anthropic-version"].FirstOrDefault() ?? "2023-06-01";
        upstreamReq.Headers.TryAddWithoutValidation("anthropic-version", anthropicVersion);
        ForwardClientIdentityHeaders(ctx.Request, upstreamReq);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var preview = bodyToSend.Length > 500 ? Encoding.UTF8.GetString(bodyToSend, 0, 500) : Encoding.UTF8.GetString(bodyToSend);
            _logger.LogDebug("DIAG upstream req {Method} {FullUrl} originalModel={Orig} upstreamModel={Up} bodyLen={BodyLen} body={Body}",
                ctx.Request.Method, fullUrl, originalModel, upstreamModel, bodyToSend.Length, preview);
        }

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

        // 5. Handle response — streaming or non-streaming.
        //    HttpResponseMessage must be disposed to return the underlying
        //    HTTP connection to the pool.  Without this, each client
        //    disconnect leaks a connection and causes socket exhaustion
        //    → thread pool starvation (Kestrel heartbeat warnings).
        using (upstreamResp)
        {
            if (clientWantsStream && upstreamResp.IsSuccessStatusCode)
            {
                await StreamSseResponse(ctx, upstreamResp, originalModel, upstreamModel, requestBody, path, sw, role);
            }
            else
            {
                try
                {
                    await HandleNonStreaming(ctx, upstreamResp, originalModel, upstreamModel, requestBody, path, sw, role);
                }
                catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("Proxy client disconnected {Method} {Path} {Original}->{Upstream} {DurationMs}ms{Role}",
                        ctx.Request.Method, path, originalModel ?? "-", upstreamModel ?? "-",
                        sw.ElapsedMilliseconds, role is not null ? $" [{role}]" : "");
                }
            }
        }
    }

    private static string? GetModel(JsonElement root) =>
        root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
            ? model.GetString()
            : null;

    private void LogClassifierDiagnostics(JsonElement root, bool isClassifier)
    {
        if (!root.TryGetProperty("system", out var system) || system.ValueKind != JsonValueKind.Array)
            return;

        var previews = new List<string>();
        foreach (var block in system.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString() is { } value)
            {
                previews.Add(value.Length > 80 ? value[..80] + "…" : value);
            }
        }
        _logger.LogDebug("DIAG classifier system array[{Count}]: texts={Previews} isClassifier={IsClassifier}",
            previews.Count, string.Join(" | ", previews), isClassifier);
    }

    // Re-serializes the request body with the model rewritten and (for classifier
    // requests) the x-anthropic-billing-header system block stripped.  Every other
    // property is copied verbatim via JsonElement.WriteTo, so only the mutated spots
    // are touched.  Root is guaranteed to be a JSON object by the caller.
    internal static void WriteTransformed(Utf8JsonWriter writer, JsonElement root, string? newModel, bool stripBillingHeader, bool stripToolChoice = false)
    {
        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("model") && newModel is not null)
            {
                writer.WriteString("model", newModel);
            }
            else if (stripToolChoice && prop.NameEquals("tool_choice"))
            {
                continue;
            }
            else if (stripBillingHeader && prop.NameEquals("system") && prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("system");
                writer.WriteStartArray();
                foreach (var block in prop.Value.EnumerateArray())
                {
                    if (!IsBillingHeaderBlock(block))
                        block.WriteTo(writer);
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    internal static bool HasWebSearchTool(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
                continue;
            if (!tool.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                continue;

            var toolType = type.GetString();
            if (toolType is not null && toolType.StartsWith("web_search_", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsBillingHeaderBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return false;
        if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return false;
        var value = text.GetString();
        return value is not null && value.StartsWith("x-anthropic-billing-header:", StringComparison.Ordinal);
    }

    private async Task HandleNonStreaming(
        HttpContext ctx,
        HttpResponseMessage upstreamResp,
        string? originalModel,
        string? upstreamModel,
        string requestBody,
        string path,
        Stopwatch sw,
        string? role)
    {
        // Read upstream response as raw bytes (non-streaming or error).
        var respBytes = await upstreamResp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        var respToSend = ErrorResponseNormalizer.Normalize(respBytes, !upstreamResp.IsSuccessStatusCode);
        var needsRewrite = originalModel is not null && upstreamModel is not null && upstreamModel != originalModel;

        // ── Diagnostic logging ──
        if (upstreamResp.IsSuccessStatusCode)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var preview = respBytes.Length > 500 ? Encoding.UTF8.GetString(respBytes, 0, 500) : Encoding.UTF8.GetString(respBytes);
                _logger.LogDebug("DIAG upstream status={Status} content-type={ContentType} bodyLen={BodyLen} body={Body}",
                    (int)upstreamResp.StatusCode, upstreamResp.Content.Headers.ContentType, respBytes.Length, preview);
            }
        }
        else
        {
            _logger.LogInformation("DIAG upstream ERROR status={Status} body={Body}",
                (int)upstreamResp.StatusCode, respBytes.Length > 1000 ? Encoding.UTF8.GetString(respBytes, 0, 1000) : Encoding.UTF8.GetString(respBytes));
        }

        // Rewrite response model back (e.g. DeepSeek → original Claude name).
        if (needsRewrite && respBytes.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(respBytes);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("model", out _))
                {
                    using var outStream = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(outStream))
                    {
                        WriteTransformed(writer, root, originalModel!, stripBillingHeader: false);
                    }
                    respToSend = outStream.ToArray();
                }
            }
            catch (JsonException)
            {
                // Response not valid JSON — forward as-is
            }
        }

        // Compliance log (non-blocking via Channel).  Decode the response body only
        // when logging is enabled, so it isn't materialized otherwise.
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
            ResponseBody = _complianceWriter.IsEnabled ? Encoding.UTF8.GetString(respBytes) : string.Empty
        });

        // Return proxied response
        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;

        foreach (var h in upstreamResp.Headers)
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in upstreamResp.Content.Headers)
            ctx.Response.Headers.TryAdd(h.Key, h.Value.ToArray());

        // Remove transfer-encoding and content-length — body length changes after model rewrite
        ctx.Response.Headers.Remove("transfer-encoding");
        ctx.Response.Headers.Remove("content-length");

        ctx.Response.ContentType = upstreamResp.Content.Headers.ContentType?.ToString() ?? "application/json";

        _logger.LogDebug("DIAG response content-type={ContentType} bodyLen={BodyLen}",
            ctx.Response.ContentType, respToSend.Length);

        await ctx.Response.Body.WriteAsync(respToSend, 0, respToSend.Length, ctx.RequestAborted);

        _logger.LogInformation("Proxy {Method} {Path} {Original}->{Upstream} {StatusCode} {DurationMs}ms{Role}",
            ctx.Request.Method, path, originalModel ?? "-", upstreamModel ?? "-",
            (int)upstreamResp.StatusCode, sw.ElapsedMilliseconds,
            role is not null ? $" [{role}]" : "");
    }

    private async Task StreamSseResponse(
        HttpContext ctx,
        HttpResponseMessage upstreamResp,
        string? originalModel,
        string? upstreamModel,
        string requestBody,
        string path,
        Stopwatch sw,
        string? role)
    {
        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
        ctx.Response.Headers["Connection"] = "keep-alive";

        using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ctx.RequestAborted);
        using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
        var doRewrite = originalModel is not null && upstreamModel is not null && upstreamModel != originalModel;

        try
        {
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
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Proxy SSE client disconnected {Method} {Path} {Original}->{Upstream} {DurationMs}ms{Role}",
                ctx.Request.Method, path, originalModel ?? "-", upstreamModel ?? "-",
                sw.ElapsedMilliseconds, role is not null ? $" [{role}]" : "");
            return;
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

        _logger.LogInformation("Proxy SSE {Method} {Path} {Original}->{Upstream} {StatusCode} {DurationMs}ms{Role}",
            ctx.Request.Method, path, originalModel ?? "-", upstreamModel ?? "-",
            (int)upstreamResp.StatusCode, sw.ElapsedMilliseconds,
            role is not null ? $" [{role}]" : "");
    }
}
