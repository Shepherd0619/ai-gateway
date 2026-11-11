using System.Text.Json;

namespace AiGateway.Http;

internal static class ApiErrorResponse
{
    public static Task WriteAsync(HttpContext ctx, int status, string type, string message, bool noStore = false) =>
        WriteAsync(ctx, status, Create(type, message), noStore);

    public static Task WriteAnthropicAsync(HttpContext ctx, int status, string message) =>
        WriteAsync(ctx, status, CreateAnthropic(status, message));

    // Also used for buffered upstream errors and SSE error events, where the caller owns the HTTP response.
    public static byte[] CreateAnthropic(int status, string message) => Create(status switch
    {
        400 => "invalid_request_error",
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        413 => "request_too_large",
        429 => "rate_limit_error",
        529 => "overloaded_error",
        _ => "api_error"
    }, message, anthropic: true);

    private static byte[] Create(string type, string message, bool anthropic = false)
    {
        var error = new { type, message };
        return anthropic
            ? JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error })
            : JsonSerializer.SerializeToUtf8Bytes(new { error });
    }

    private static async Task WriteAsync(HttpContext ctx, int status, byte[] body, bool noStore = false)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        if (noStore) ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
    }
}
