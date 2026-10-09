using System.Text.Json.Serialization;
using AiGateway.Configuration;
using AiGateway.Proxy;

namespace AiGateway.Discovery;

internal static class ModelDiscoveryEndpoints
{
    internal static void MapModelDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", HandleModels);
    }

    internal static async Task HandleModels(HttpContext ctx, RuntimeModelDiscoveryStore store, ProxyHandler proxy)
    {
        var snapshot = store.Snapshot;
        if (!snapshot.Enabled || !HasClientApiKey(ctx.Request))
        {
            await proxy.Invoke(ctx);
            return;
        }

        if (ctx.Request.Headers.ContainsKey("anthropic-version"))
        {
            await ctx.Response.WriteAsJsonAsync(ToAnthropicResponse(snapshot.Models), ctx.RequestAborted);
            return;
        }

        await ctx.Response.WriteAsJsonAsync(ToOpenAiResponse(snapshot.Models), ctx.RequestAborted);
    }

    private static bool HasClientApiKey(HttpRequest request)
    {
        if (!string.IsNullOrEmpty(request.Headers["x-api-key"].FirstOrDefault()))
            return true;

        var authorization = request.Headers.Authorization.FirstOrDefault();
        return authorization is not null &&
               authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(authorization["Bearer ".Length..]);
    }

    internal static object ToOpenAiResponse(IReadOnlyList<ModelDiscoveryModel> models) => new OpenAiModelList(
        "list",
        models.Select(model => new OpenAiModel(
            model.Id,
            "model",
            model.CreatedAt?.ToUnixTimeSeconds() ?? 0,
            model.OwnedBy ?? "ai-gateway")).ToArray());

    internal static object ToAnthropicResponse(IReadOnlyList<ModelDiscoveryModel> models)
    {
        var data = models.Select(model => new AnthropicModel(
            model.Id,
            "model",
            model.DisplayName ?? model.Id,
            model.CreatedAt)).ToArray();

        return new AnthropicModelList(data, false, data.FirstOrDefault()?.Id, data.LastOrDefault()?.Id);
    }

    private sealed record OpenAiModelList(
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("data")] IReadOnlyList<OpenAiModel> Data);

    private sealed record OpenAiModel(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("created")] long Created,
        [property: JsonPropertyName("owned_by")] string OwnedBy);

    private sealed record AnthropicModelList(
        [property: JsonPropertyName("data")] IReadOnlyList<AnthropicModel> Data,
        [property: JsonPropertyName("has_more")] bool HasMore,
        [property: JsonPropertyName("first_id")] string? FirstId,
        [property: JsonPropertyName("last_id")] string? LastId);

    private sealed record AnthropicModel(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("created_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CreatedAt);
}
