using System.Text;
using System.Text.Json;
using AiGateway.Http;

namespace AiGateway.Proxy;

// One instance per request. Tool argument deltas are forwarded as they arrive;
// only metadata, early parallel-tool fragments and the current SSE event are buffered.
internal sealed class AnthropicSseTranslator(HttpResponse response, string model, CancellationToken cancellationToken)
{
    private bool _started;
    private bool _completed;
    private string? _finish;
    private int _nextIndex;
    private int? _textIndex;
    private string? _textType;
    private AnthropicTranslator.Usage _usage;
    private readonly SortedDictionary<int, ToolState> _tools = new();

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
            if (!_completed) await CompleteAsync();
        }
        catch (Exception ex) when (ex is JsonException or TranslationException or InvalidOperationException or IOException && !cancellationToken.IsCancellationRequested)
        {
            await EmitErrorAsync("Upstream returned an invalid or incomplete Chat Completions stream.");
        }
    }

    internal async Task ProcessAsync(string data)
    {
        if (data == "[DONE]") { await CompleteAsync(); return; }
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        AnthropicTranslator.Require(root.ValueKind == JsonValueKind.Object, "Invalid SSE data.");
        if (root.TryGetProperty("error", out var upstreamError))
        {
            var status = 502;
            if (upstreamError.ValueKind == JsonValueKind.Object && upstreamError.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var errorCode) && errorCode >= 400 && errorCode <= 599)
                status = errorCode;
            await EmitErrorAsync(AnthropicTranslator.ErrorMessage(root, "Upstream stream failed."), status); return;
        }
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) _usage = AnthropicTranslator.ReadUsage(root);
        AnthropicTranslator.Require(root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array, "Missing stream choices.");
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("index", out var choiceIndex) && choiceIndex.GetInt32() != 0) continue;
            if (!_started)
            {
                await EmitAsync("message_start", w =>
                {
                    w.WritePropertyName("message"); w.WriteStartObject();
                    AnthropicTranslator.WriteMessageHeader(w, AnthropicTranslator.Text(root, "id"), model);
                    w.WritePropertyName("content"); w.WriteStartArray(); w.WriteEndArray();
                    w.WriteNull("stop_reason"); w.WriteNull("stop_sequence");
                    w.WritePropertyName("usage"); AnthropicTranslator.WriteUsage(w, _usage); w.WriteEndObject();
                });
                _started = true;
            }
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                var reasoning = AnthropicTranslator.Text(delta, "reasoning_content") ?? AnthropicTranslator.Text(delta, "reasoning");
                if (!string.IsNullOrEmpty(reasoning)) await TextAsync("thinking", reasoning);
                if (AnthropicTranslator.Text(delta, "content") is { Length: > 0 } text) await TextAsync("text", text);
                if (AnthropicTranslator.Text(delta, "refusal") is { Length: > 0 } refusal) await TextAsync("text", refusal);
                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    await CloseTextAsync();
                    foreach (var call in calls.EnumerateArray()) ReadTool(call);
                    await FlushToolsAsync(final: false);
                }
            }
            if (AnthropicTranslator.Text(choice, "finish_reason") is { } finish) _finish = AnthropicTranslator.StopReason(finish);
        }
    }

    private async Task TextAsync(string type, string text)
    {
        if (_textIndex is null || _textType != type)
        {
            await CloseTextAsync();
            _textIndex = _nextIndex++; _textType = type;
            await EmitAsync("content_block_start", w =>
            {
                w.WriteNumber("index", _textIndex.Value); w.WritePropertyName("content_block"); w.WriteStartObject();
                w.WriteString("type", type); w.WriteString(type, "");
                if (type == "thinking") w.WriteString("signature", "");
                w.WriteEndObject();
            });
        }
        await EmitAsync("content_block_delta", w =>
        {
            w.WriteNumber("index", _textIndex.Value); w.WritePropertyName("delta"); w.WriteStartObject();
            w.WriteString("type", type == "thinking" ? "thinking_delta" : "text_delta"); w.WriteString(type, text); w.WriteEndObject();
        });
    }

    private async Task CloseTextAsync()
    {
        if (_textIndex is not { } index) return;
        await EmitAsync("content_block_stop", w => w.WriteNumber("index", index));
        _textIndex = null;
    }

    private void ReadTool(JsonElement call)
    {
        AnthropicTranslator.Require(call.TryGetProperty("index", out var indexEl) && indexEl.TryGetInt32(out _), "Missing tool index.");
        var index = indexEl.GetInt32();
        if (!_tools.TryGetValue(index, out var tool)) _tools[index] = tool = new ToolState();
        if (AnthropicTranslator.Text(call, "id") is { } id) tool.Id.Append(id);
        string? args = null;
        if (call.TryGetProperty("function", out var function))
        {
            if (AnthropicTranslator.Text(function, "name") is { } name) tool.Name.Append(name);
            args = AnthropicTranslator.Text(function, "arguments");
        }
        if (!string.IsNullOrEmpty(args)) tool.PendingArguments.Add(args);
    }

    private async Task FlushToolsAsync(bool final)
    {
        foreach (var tool in _tools.Values)
        {
            if (tool.BlockIndex is null)
            {
                // A later parallel call may send arguments before an earlier
                // call finishes its metadata. Queue only those early fragments.
                if (!final && tool.PendingArguments.Count == 0) break;
                if (!final && (tool.Id.Length == 0 || tool.Name.Length == 0)) break;
                await StartToolAsync(tool);
            }
            foreach (var args in tool.PendingArguments)
            {
                await EmitAsync("content_block_delta", w =>
                {
                    w.WriteNumber("index", tool.BlockIndex!.Value); w.WritePropertyName("delta"); w.WriteStartObject();
                    w.WriteString("type", "input_json_delta"); w.WriteString("partial_json", args); w.WriteEndObject();
                });
            }
            tool.PendingArguments.Clear();
        }
    }
    private async Task StartToolAsync(ToolState tool)
    {
        AnthropicTranslator.Require(tool.Id.Length > 0 && tool.Name.Length > 0, "Missing tool name or id.");
        tool.BlockIndex = _nextIndex++;
        await EmitAsync("content_block_start", w =>
        {
            w.WriteNumber("index", tool.BlockIndex!.Value); w.WritePropertyName("content_block"); w.WriteStartObject();
            w.WriteString("type", "tool_use"); w.WriteString("id", tool.Id.ToString()); w.WriteString("name", tool.Name.ToString());
            w.WritePropertyName("input"); w.WriteStartObject(); w.WriteEndObject(); w.WriteEndObject();
        });
    }

    internal async Task CompleteAsync(string? stopSequence = null)
    {
        AnthropicTranslator.Require(_started && _finish is not null, "Upstream stream ended before a finish_reason.");
        if (stopSequence is not null) _finish = "stop_sequence";
        await CloseTextAsync();
        await FlushToolsAsync(final: true);
        foreach (var tool in _tools.Values)
        {
            if (tool.BlockIndex is null) await StartToolAsync(tool);
            await EmitAsync("content_block_stop", w => w.WriteNumber("index", tool.BlockIndex!.Value));
        }
        await EmitAsync("message_delta", w =>
        {
            w.WritePropertyName("delta"); w.WriteStartObject(); w.WriteString("stop_reason", _finish); w.WriteString("stop_sequence", stopSequence); w.WriteEndObject();
            w.WritePropertyName("usage"); AnthropicTranslator.WriteUsage(w, _usage);
        });
        await EmitAsync("message_stop", _ => { });
        _completed = true;
    }

    internal async Task EmitErrorAsync(string message, int status = 502)
    {
        var bytes = ApiErrorResponse.CreateAnthropic(status, message);
        await response.WriteAsync("event: error\ndata: " + Encoding.UTF8.GetString(bytes) + "\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        _completed = true;
    }

    internal async Task SignatureAsync(string signature)
    {
        if (_textIndex is null || _textType != "thinking") await TextAsync("thinking", "");
        await EmitAsync("content_block_delta", w =>
        {
            w.WriteNumber("index", _textIndex!.Value); w.WritePropertyName("delta"); w.WriteStartObject();
            w.WriteString("type", "signature_delta"); w.WriteString("signature", signature); w.WriteEndObject();
        });
        await CloseTextAsync();
    }

    private async Task EmitAsync(string type, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject(); w.WriteString("type", type); write(w); w.WriteEndObject();
        }
        await response.WriteAsync("event: " + type + "\ndata: " + Encoding.UTF8.GetString(buffer.ToArray()) + "\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private sealed class ToolState
    {
        public StringBuilder Id { get; } = new();
        public StringBuilder Name { get; } = new();
        public int? BlockIndex;
        public List<string> PendingArguments { get; } = new();
    }
}
