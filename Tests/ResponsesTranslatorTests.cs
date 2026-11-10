using System.Text;
using System.Text.Json;
using AiGateway.Proxy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AiGateway.Tests;

public sealed class ResponsesTranslatorTests
{
    private const string Reasoning = """{"type":"reasoning","id":"rs-a","summary":[{"type":"summary_text","text":"Inspect the file"}],"encrypted_content":"opaque-ciphertext"}""";
    private const string Function = """{"type":"function_call","id":"fc-a","call_id":"call-a","name":"Read","arguments":"{\"path\":\"a.cs\"}"}""";
    private static string Result(string status = "completed", string? details = null) => "{\"id\":\"resp-a\",\"status\":\"" + status + "\",\"output\":[" + Reasoning + "," + Function + "],\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":2}}" + (details is null ? "" : ",\"incomplete_details\":{\"reason\":\"" + details + "\"}") + "}";
    private static JsonDocument Request(string body) => JsonDocument.Parse(AnthropicResponsesTranslator.TranslateRequest(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Request_UsesResponsesReasoningToolsImagesAndOutputLimit()
    {
        using var doc = Request("""
        {"model":"gpt-test","max_tokens":4096,"stream":true,"thinking":{"type":"adaptive"},"output_config":{"effort":"medium"},"temperature":1,
        "system":"Be helpful","tools":[{"name":"Read","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}],
        "tool_choice":{"type":"tool","name":"Read","disable_parallel_tool_use":true},
        "messages":[{"role":"user","content":[{"type":"text","text":"Look"},{"type":"image","source":{"type":"url","url":"https://example.test/a.png"}}]},{"role":"system","content":"Reminder"}]}
        """);
        var root = doc.RootElement;
        Assert.Equal(4096, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", root.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(root.TryGetProperty("stream_options", out _));
        Assert.True(root.GetProperty("stream").GetBoolean());
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("Read", tool.GetProperty("name").GetString());
        Assert.False(tool.GetProperty("strict").GetBoolean());
        Assert.True(tool.GetProperty("parameters").GetProperty("properties").TryGetProperty("path", out _));
        Assert.Equal("Read", root.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        var input = root.GetProperty("input");
        Assert.Equal("system", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("input_image", input[1].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("Reminder", input[2].GetProperty("content").GetString());
    }

    [Fact]
    public void ReasoningAndToolResponse_ReplaysEncryptedStateBeforeCallsAndResults()
    {
        using var response = JsonDocument.Parse(AnthropicResponsesTranslator.TranslateResponse(Encoding.UTF8.GetBytes(Result()), "claude-client"));
        var root = response.RootElement;
        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
        Assert.Equal(8, root.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, root.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32());
        Assert.Equal(5, root.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        Assert.Equal("Inspect the file", root.GetProperty("content")[0].GetProperty("thinking").GetString());
        Assert.Equal("a.cs", root.GetProperty("content")[1].GetProperty("input").GetProperty("path").GetString());
        using var next = Request("{\"model\":\"gpt-test\",\"max_tokens\":4096,\"messages\":[{\"role\":\"user\",\"content\":\"Inspect\"},{\"role\":\"assistant\",\"content\":" + root.GetProperty("content").GetRawText() + "},{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"call-a\",\"content\":\"file content\"}]}]}");
        var input = next.RootElement.GetProperty("input");
        Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
        Assert.Equal("opaque-ciphertext", input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("function_call", input[2].GetProperty("type").GetString());
        Assert.Equal("call-a", input[2].GetProperty("call_id").GetString());
        Assert.Equal("function_call_output", input[3].GetProperty("type").GetString());
        Assert.Equal("call-a", input[3].GetProperty("call_id").GetString());
        Assert.Equal("file content", input[3].GetProperty("output").GetString());
    }

    [Theory]
    [InlineData("max_output_tokens", "max_tokens")]
    [InlineData("content_filter", "refusal")]
    public void IncompleteResponses_MapStopReason(string reason, string expected)
    {
        using var response = JsonDocument.Parse(AnthropicResponsesTranslator.TranslateResponse(Encoding.UTF8.GetBytes(Result("incomplete", reason)), "m"));
        Assert.Equal(expected, response.RootElement.GetProperty("stop_reason").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"STOP\"")]
    [InlineData("[1]")]
    [InlineData("[\"\"]")]
    public void MalformedStopSequences_FailExplicitly(string sequences)
    {
        Assert.Throws<TranslationException>(() => Request("{\"model\":\"m\",\"max_tokens\":10,\"stop_sequences\":" + sequences + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"));
    }

    private static Task<JsonElement[]> Stream(params string[] data) => StreamWithStops([], data);

    private static async Task<JsonElement[]> StreamWithStops(string[] stops, params string[] data)
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        var sse = string.Join("", data.Select(d => "data: " + d + "\n\n"));
        await new AnthropicResponsesSseTranslator(context.Response, "claude-client", CancellationToken.None, stops).RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        return Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()).Split('\n')
            .Where(l => l.StartsWith("data: ")).Select(l => JsonDocument.Parse(l[6..]).RootElement.Clone()).ToArray();
    }

    [Fact]
    public void StopSequences_OnlyMatchAnswerTextAndSuppressFollowingToolCalls()
    {
        var body = "{\"status\":\"completed\",\"output\":[" + Reasoning + ",{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"allow</dec\"},{\"type\":\"output_text\",\"text\":\"ision>hidden\"}]}," + Function + "],\"usage\":{\"input_tokens\":4,\"output_tokens\":8}}";
        using var response = JsonDocument.Parse(AnthropicResponsesTranslator.TranslateResponse(Encoding.UTF8.GetBytes(body), "m", ["</decision>", "Inspect"]));
        var root = response.RootElement;
        Assert.Equal("thinking", Text(root.GetProperty("content")[0], "type"));
        Assert.Equal("allow", root.GetProperty("content")[1].GetProperty("text").GetString());
        Assert.Equal(2, root.GetProperty("content").GetArrayLength());
        Assert.Equal("</decision>", root.GetProperty("stop_sequence").GetString());
        Assert.Equal(8, root.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Theory]
    [InlineData("allow</dec", "allow</dec", "end_turn")]
    [InlineData("allow</decision>hidden", "allow", "stop_sequence")]
    public async Task StopStream_FinalOutputFallbackAndUnmatchedPrefixesArePreserved(string text, string expected, string reason)
    {
        var events = await StreamWithStops(["</decision>"],
            JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } } }));
        var visible = string.Concat(events.Where(e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "text_delta")
            .Select(e => e.GetProperty("delta").GetProperty("text").GetString()));
        Assert.Equal(expected, visible);
        Assert.Equal(reason, events[^2].GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Equal("message_stop", Text(events[^1], "type"));
    }

    [Fact]
    public async Task Stream_EmitsThinkingSignatureIncrementalArgumentsAndUsage()
    {
        var events = await Stream(
            """{"type":"response.created","response":{"id":"resp-a"}}""",
            """{"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"Inspect the file"}""",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":" + Reasoning + "}",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call-a","name":"Read","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"path\":"}""",
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"\"a.cs\"}"}""",
            "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":" + Function + "}",
            "{\"type\":\"response.completed\",\"response\":" + Result() + "}");
        Assert.Equal("message_start", events[0].GetProperty("type").GetString());
        Assert.Single(events, e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "thinking_delta");
        var signature = events.Single(e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "signature_delta").GetProperty("delta").GetProperty("signature").GetString();
        Assert.StartsWith("gateway.responses.v1:", signature);
        Assert.Equal(new[] { "{\"path\":", "\"a.cs\"}" }, events.Where(e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "input_json_delta").Select(e => e.GetProperty("delta").GetProperty("partial_json").GetString()));
        Assert.Equal("tool_use", events[^2].GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Equal(8, events[^2].GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal("message_stop", events[^1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task TextStream_DoesNotDuplicateCompletedText()
    {
        var events = await Stream("""{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"Hello"}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Hello"}]}]}}""");
        Assert.Equal("Hello", string.Concat(events.Where(e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "text_delta").Select(e => e.GetProperty("delta").GetProperty("text").GetString())));
        Assert.Equal("end_turn", events[^2].GetProperty("delta").GetProperty("stop_reason").GetString());
    }

    [Fact]
    public async Task ParallelFunctionStream_KeepsCallIdsAndArgumentsSeparate()
    {
        const string first = """{"type":"function_call","call_id":"a","name":"Read","arguments":"{\"path\":\"a\"}"}""";
        const string second = """{"type":"function_call","call_id":"b","name":"Read","arguments":"{\"path\":\"b\"}"}""";
        var events = await Stream(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"a","name":"Read"}}""",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"b","name":"Read"}}""",
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"path\":\"b\"}"}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"path\":\"a\"}"}""",
            "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[" + first + "," + second + "]}}");
        var starts = events.Where(e => Text(e, "type") == "content_block_start").ToArray();
        Assert.Equal(new[] { "a", "b" }, starts.Select(e => e.GetProperty("content_block").GetProperty("id").GetString()));
        foreach (var (index, path) in new[] { (0, "a"), (1, "b") })
        {
            var arguments = string.Concat(events.Where(e => Text(e, "type") == "content_block_delta" && e.GetProperty("index").GetInt32() == index)
                .Select(e => e.GetProperty("delta").GetProperty("partial_json").GetString()));
            using var args = JsonDocument.Parse(arguments);
            Assert.Equal(path, args.RootElement.GetProperty("path").GetString());
        }
    }

    [Fact]
    public async Task InvalidStreamedFunctionArguments_FailInsteadOfReportingSuccess()
    {
        var events = await Stream(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"a","name":"Read"}}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{"}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","call_id":"a","name":"Read","arguments":"{"}]}}""");
        Assert.Equal("error", Text(events[^1], "type"));
        Assert.DoesNotContain(events, e => Text(e, "type") == "message_stop");
    }

    [Fact]
    public async Task ConsecutiveReasoningItems_KeepSeparateReplaySignatures()
    {
        var events = await Stream("{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[" + Reasoning + "," + Reasoning + "]}}");
        var signatures = events.Where(e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "signature_delta").ToArray();
        Assert.Equal(2, signatures.Length);
        Assert.NotEqual(signatures[0].GetProperty("index").GetInt32(), signatures[1].GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task ReasoningEncryptionInTerminalEvent_IsPreservedWithoutDuplicatingSummary()
    {
        var events = await Stream(
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs-a","summary":[{"type":"summary_text","text":"Inspect the file"}]}}""",
            "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[" + Reasoning + "]}}");
        var signature = events.Single(e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "signature_delta").GetProperty("delta").GetProperty("signature").GetString()!;
        using var item = JsonDocument.Parse(Convert.FromBase64String(signature["gateway.responses.v1:".Length..]));
        Assert.Equal("opaque-ciphertext", item.RootElement.GetProperty("encrypted_content").GetString());
        Assert.Single(events, e => e.TryGetProperty("delta", out var d) && Text(d, "type") == "thinking_delta");
    }

    [Fact]
    public async Task ChangedFunctionIdentity_EmitsError()
    {
        var events = await Stream(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"a","name":"Read"}}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","call_id":"b","name":"Write","arguments":"{}"}]}}""");
        Assert.Equal("error", Text(events[^1], "type"));
        Assert.DoesNotContain(events, e => Text(e, "type") == "message_stop");
    }

    [Fact]
    public void MalformedGatewaySignature_IsRejected()
    {
        Assert.Throws<TranslationException>(() => Request("""{"model":"m","max_tokens":10,"messages":[{"role":"assistant","content":[{"type":"thinking","thinking":"plan","signature":"gateway.responses.v1:invalid"}]}]}"""));
    }

    [Theory]
    [InlineData("{\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"Partial\"}")]
    [InlineData("{\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"Rate limited\",\"code\":\"rate_limit_exceeded\"}}}")]
    [InlineData("{invalid")]
    public async Task BrokenResponsesStreams_EmitErrorWithoutSuccessfulStop(string data)
    {
        var events = await Stream(data);
        Assert.Equal("error", events[^1].GetProperty("type").GetString());
        Assert.DoesNotContain(events, e => Text(e, "type") == "message_stop");
    }

    private static string? Text(JsonElement root, string name) => AnthropicTranslator.Text(root, name);
}
