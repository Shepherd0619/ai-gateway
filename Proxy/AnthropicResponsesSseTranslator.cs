using System.Text;
using System.Text.Json;
using static AiGateway.Proxy.AnthropicTranslator;

namespace AiGateway.Proxy;

// Normalize Responses lifecycle events into the existing incremental Anthropic
// event writer. Only the current event and per-call argument fragments are held.
internal sealed class AnthropicResponsesSseTranslator(HttpResponse response, string model, CancellationToken cancellationToken,
    IReadOnlyList<string>? stopSequences = null)
{
    private readonly StopSequenceMatcher _stop = new(stopSequences);
    private readonly AnthropicSseTranslator _writer = new(response, model, cancellationToken);
    private readonly Dictionary<int, FunctionState> _functions = new();
    private readonly HashSet<(int, int)> _textParts = new();
    private readonly HashSet<int> _reasoningDeltas = new();
    private readonly HashSet<int> _signedReasoning = new();
    private string? _id;
    private bool _completed;

    internal async Task RunAsync(Stream upstream)
    {
        using var reader = new StreamReader(upstream, Encoding.UTF8);
        var data = new StringBuilder();
        try
        {
            while (!_completed && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0) { await ProcessAsync(data.ToString()); data.Clear(); }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                }
            }
            if (!_completed && data.Length > 0) await ProcessAsync(data.ToString());
            if (!_completed) await _writer.EmitErrorAsync("Responses stream ended before completion.");
        }
        catch (Exception ex) when (ex is JsonException or TranslationException or InvalidOperationException or KeyNotFoundException or IOException && !cancellationToken.IsCancellationRequested)
        { await _writer.EmitErrorAsync("Upstream returned an invalid or incomplete Responses stream."); }
    }

    private async Task ProcessAsync(string data)
    {
        if (data == "[DONE]") return; // Responses needs a terminal response event, not this sentinel.
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        switch (Text(root, "type"))
        {
            case "response.created":
                _id = Text(root.GetProperty("response"), "id"); await ChunkAsync(new { }); break;
            case "response.output_item.added":
                if (_stop.Matched is null && Text(root.GetProperty("item"), "type") == "function_call")
                {
                    await FlushTextAsync();
                    if (_stop.Matched is null) await FunctionAsync(Index(root), root.GetProperty("item"));
                }
                break;
            case "response.output_text.delta": case "response.refusal.delta":
                _textParts.Add((Index(root), ContentIndex(root)));
                await ChunkAsync(new { content = _stop.Append(Text(root, "delta") ?? "") }); break;
            case "response.reasoning_summary_text.delta": case "response.reasoning_text.delta":
                if (_stop.Matched is not null) break;
                await FlushTextAsync();
                if (_stop.Matched is not null) break;
                _reasoningDeltas.Add(Index(root));
                await ChunkAsync(new { reasoning_content = Text(root, "delta") ?? "" }); break;
            case "response.function_call_arguments.delta":
                if (_stop.Matched is not null) break;
                Require(_functions.TryGetValue(Index(root), out var state), "Missing Responses function metadata.");
                var fragment = Text(root, "delta") ?? "";
                state!.Arguments.Append(fragment); await ArgumentsAsync(Index(root), fragment); break;
            case "response.function_call_arguments.done":
                if (_stop.Matched is not null) break;
                await FinishArgumentsAsync(Index(root), Text(root, "arguments")); break;
            case "response.output_item.done":
                await ItemDoneAsync(Index(root), root.GetProperty("item")); break;
            case "response.completed": case "response.incomplete":
                var result = root.GetProperty("response"); _id ??= Text(result, "id");
                var finish = AnthropicResponsesTranslator.Finish(result);
                Require(result.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array, "Missing Responses output.");
                var index = 0;
                foreach (var item in items.EnumerateArray()) await ItemDoneAsync(index++, item, terminal: true);
                await FlushTextAsync();
                foreach (var function in _functions.Values)
                {
                    using var arguments = JsonDocument.Parse(function.Arguments.ToString());
                    Require(arguments.RootElement.ValueKind == JsonValueKind.Object, "Invalid Responses function arguments.");
                }
                await ChunkAsync(new { }, finish, AnthropicResponsesTranslator.Usage(result));
                await _writer.CompleteAsync(_stop.Matched); _completed = true; break;
            case "response.failed": case "error":
                var errorRoot = root.TryGetProperty("response", out var failed) ? failed : root;
                var error = errorRoot.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : errorRoot;
                var status = Text(error, "code") is "rate_limit_exceeded" or "rate_limit_error" ? 429 : 502;
                await _writer.EmitErrorAsync(ErrorMessage(errorRoot, "Responses request failed."), status); _completed = true; break;
        }
    }

    private async Task ItemDoneAsync(int index, JsonElement item, bool terminal = false)
    {
        if (_stop.Matched is not null) return;
        if (Text(item, "type") != "message") await FlushTextAsync();
        if (_stop.Matched is not null) return;
        switch (Text(item, "type"))
        {
            case "reasoning":
                if (_signedReasoning.Contains(index)) return;
                if (_reasoningDeltas.Add(index) && item.TryGetProperty("summary", out var summary))
                    foreach (var part in summary.EnumerateArray())
                        if (Text(part, "text") is { } text) await ChunkAsync(new { reasoning_content = text });
                // Some compatible providers attach encrypted state only to the
                // terminal response. Never commit an earlier incomplete item.
                if (!terminal && string.IsNullOrEmpty(Text(item, "encrypted_content"))) return;
                _signedReasoning.Add(index);
                await ChunkAsync(new { });
                await _writer.SignatureAsync(AnthropicResponsesTranslator.Signature(item)); break;
            case "message":
                var partIndex = 0;
                foreach (var part in item.GetProperty("content").EnumerateArray())
                {
                    Require(Text(part, "type") is "output_text" or "refusal", "Unsupported Responses content.");
                    if (_textParts.Add((index, partIndex++)))
                        await ChunkAsync(new { content = _stop.Append(Text(part, "text") ?? Text(part, "refusal") ?? "") });
                }
                break;
            case "function_call":
                await FunctionAsync(index, item);
                await FinishArgumentsAsync(index, Text(item, "arguments")); break;
            default: throw new TranslationException("Unsupported Responses output item.");
        }
    }

    private async Task FunctionAsync(int index, JsonElement item)
    {
        var id = Text(item, "call_id"); var name = Text(item, "name");
        Require(!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name), "Invalid Responses function metadata.");
        if (_functions.TryGetValue(index, out var existing))
        {
            Require(existing.Id == id && existing.Name == name, "Responses function identity changed after streaming.");
            return;
        }
        _functions[index] = new(id!, name!);
        await ChunkAsync(new { tool_calls = new[] { new { index, id, type = "function", function = new { name, arguments = "" } } } });
    }

    private async Task FinishArgumentsAsync(int index, string? complete)
    {
        Require(_functions.TryGetValue(index, out var state), "Missing Responses function metadata.");
        complete ??= state!.Arguments.Length > 0 ? state.Arguments.ToString() : "{}";
        using var args = JsonDocument.Parse(complete);
        Require(args.RootElement.ValueKind == JsonValueKind.Object, "Tool arguments must be an object.");
        if (state!.Arguments.Length == 0) { state.Arguments.Append(complete); await ArgumentsAsync(index, complete); }
        else Require(state.Arguments.ToString() == complete, "Responses function arguments changed after streaming.");
    }

    private Task ArgumentsAsync(int index, string arguments) => ChunkAsync(new { tool_calls = new[] { new { index, function = new { arguments } } } });
    private Task FlushTextAsync() => ChunkAsync(new { content = _stop.Flush() });
    private Task ChunkAsync(object delta, string? finish = null, AnthropicTranslator.Usage? usage = null)
    {
        var chunk = new Dictionary<string, object?>
        {
            ["id"] = _id,
            ["choices"] = new[] { new { index = 0, delta, finish_reason = finish } }
        };
        if (usage is { } u) chunk["usage"] = new { prompt_tokens = u.Input + u.Cached, completion_tokens = u.Output, prompt_tokens_details = new { cached_tokens = u.Cached } };
        return _writer.ProcessAsync(JsonSerializer.Serialize(chunk));
    }
    private static int Index(JsonElement root) => root.GetProperty("output_index").GetInt32();
    private static int ContentIndex(JsonElement root) => root.TryGetProperty("content_index", out var index) ? index.GetInt32() : 0;
    private sealed class FunctionState(string id, string name)
    {
        internal string Id { get; } = id;
        internal string Name { get; } = name;
        internal StringBuilder Arguments { get; } = new();
    }
}
