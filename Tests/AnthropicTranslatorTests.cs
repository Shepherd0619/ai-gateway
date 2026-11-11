using System.Text;
using System.Text.Json;
using AiGateway.Proxy;
using Microsoft.AspNetCore.Http;
using Xunit;
namespace AiGateway.Tests;

public sealed class AnthropicTranslatorTests
{
    private static JsonDocument Request(string json) => JsonDocument.Parse(AnthropicTranslator.TranslateRequest(Encoding.UTF8.GetBytes(json)));
    private static JsonDocument Response(string json) => JsonDocument.Parse(AnthropicTranslator.TranslateResponse(Encoding.UTF8.GetBytes(json), "claude-client"));

    [Theory]
    [InlineData("{\"role\":\"function\",\"content\":\"private prompt\"}", "\"function\"")]
    [InlineData("{\"role\":\"tool\",\"content\":\"private result\"}", "\"tool\"")]
    [InlineData("{\"content\":\"private text\"}", "missing")]
    [InlineData("{\"role\":17,\"content\":\"private text\"}", "Number")]
    public void InvalidMessageRole_IdentifiesInputIndexAndRoleWithoutContent(string message, string received)
    {
        var error = Assert.Throws<TranslationException>(() => Request("{\"model\":\"m\",\"max_tokens\":10,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}," + message + "]}"));
        Assert.Contains("messages[1].role", error.Message);
        Assert.Contains(received, error.Message);
        Assert.DoesNotContain("private", error.Message);
    }

    [Theory]
    [InlineData("\"Inline instruction\"", "Inline instruction")]
    [InlineData("[{\"type\":\"text\",\"text\":\"First instruction\",\"cache_control\":{\"type\":\"ephemeral\"}},{\"type\":\"text\",\"text\":\"Second instruction\"}]", "First instruction\nSecond instruction")]
    public void InlineSystemMessage_PreservesRoleTextAndConversationOrder(string content, string expected)
    {
        using var doc = Request("{\"model\":\"m\",\"max_tokens\":10,\"system\":\"Top-level instruction\",\"messages\":[{\"role\":\"user\",\"content\":\"Question\"},{\"role\":\"system\",\"content\":" + content + "},{\"role\":\"assistant\",\"content\":\"Answer\"},{\"role\":\"user\",\"content\":\"Followup\"}]}");
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(new[] { "system", "user", "system", "assistant", "user" }, messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Top-level instruction", messages[0].GetProperty("content").GetString());
        Assert.Equal("Question", messages[1].GetProperty("content").GetString());
        Assert.Equal(expected, messages[2].GetProperty("content").GetString());
        Assert.Equal("Answer", messages[3].GetProperty("content").GetString());
        Assert.Equal("Followup", messages[4].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("{\"role\":\"system\"}")]
    [InlineData("{\"role\":\"system\",\"content\":null}")]
    [InlineData("{\"role\":\"system\",\"content\":[{\"type\":\"image\"}]}")]
    public void InvalidInlineSystemContent_IsRejected(string message)
    {
        Assert.Throws<TranslationException>(() => Request("{\"model\":\"m\",\"max_tokens\":10,\"messages\":[" + message + "]}"));
    }

    [Theory]
    [InlineData(1024, "low")]
    [InlineData(1025, "medium")]
    [InlineData(8192, "medium")]
    [InlineData(8193, "high")]
    public void ThinkingBudget_MapsToOpenAiEffort(long budget, string expected)
    {
        using var doc = Request($$"""{"model":"m","max_tokens":32000,"thinking":{"type":"enabled","budget_tokens":{{budget}}},"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(expected, doc.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
        Assert.False(doc.RootElement.TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData("{\"thinking\":{\"type\":\"adaptive\"}}", "high")]
    [InlineData("{\"thinking\":{\"type\":\"disabled\"},\"output_config\":{\"effort\":\"high\"}}", "none")]
    [InlineData("{\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":1024},\"output_config\":{\"effort\":\"high\"}}", "high")]
    [InlineData("{\"thinking\":{\"type\":\"adaptive\"},\"output_config\":{\"effort\":\"low\"}}", "low")]
    [InlineData("{\"output_config\":{\"effort\":\"medium\"}}", "medium")]
    [InlineData("{\"output_config\":{\"effort\":\"xhigh\"}}", "xhigh")]
    [InlineData("{\"output_config\":{\"effort\":\"max\"}}", "max")]
    public void ThinkingModeAndEffort_TranslateWithExplicitPrecedence(string options, string expected)
    {
        using var doc = Request("{\"model\":\"m\",\"max_tokens\":32000,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," + options[1..]);
        Assert.Equal(expected, doc.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(doc.RootElement.TryGetProperty("output_config", out _));
    }

    [Fact]
    public void MissingThinkingControls_DoesNotOverrideUpstreamDefaults()
    {
        using var doc = Request("""{"model":"m","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}""");
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(doc.RootElement.TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"unknown\"}")]
    [InlineData("{\"type\":\"enabled\"}")]
    [InlineData("{\"type\":\"enabled\",\"budget_tokens\":0}")]
    [InlineData("{\"type\":\"enabled\",\"budget_tokens\":-1}")]
    [InlineData("{\"type\":\"enabled\",\"budget_tokens\":1.5}")]
    [InlineData("{\"type\":\"enabled\",\"budget_tokens\":\"1024\"}")]
    public void InvalidThinking_FailsExplicitly(string thinking)
    {
        Assert.Throws<TranslationException>(() => Request("{\"model\":\"m\",\"max_tokens\":32000,\"thinking\":" + thinking + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"extreme\"")]
    [InlineData("7")]
    public void InvalidEffort_FailsExplicitly(string effort)
    {
        Assert.Throws<TranslationException>(() => Request("{\"model\":\"m\",\"max_tokens\":32000,\"output_config\":{\"effort\":" + effort + "},\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"));
    }

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public void ThinkingToolResponse_CanBeReplayedWithToolResults(string reasoningField)
    {
        using var response = Response("""{"choices":[{"message":{"REASONING_FIELD":"Need to inspect the file","content":null,"tool_calls":[{"id":"call-a","type":"function","function":{"name":"read","arguments":"{\"path\":\"a.cs\"}"}}]},"finish_reason":"tool_calls"}]}""".Replace("REASONING_FIELD", reasoningField));
        var content = response.RootElement.GetProperty("content").GetRawText();
        using var next = Request("{\"model\":\"m\",\"max_tokens\":32000,\"thinking\":{\"type\":\"adaptive\"},\"messages\":[{\"role\":\"user\",\"content\":\"Fix the code\"},{\"role\":\"assistant\",\"content\":" + content + "},{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"call-a\",\"content\":\"source code\"}]}]}");
        var messages = next.RootElement.GetProperty("messages");
        Assert.Equal("Need to inspect the file", messages[1].GetProperty("reasoning_content").GetString());
        Assert.Equal("call-a", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("{\"path\":\"a.cs\"}", messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call-a", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("source code", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public void ToolConversation_PreservesSchemasIdsArgumentsAndResultOrder()
    {
        using var doc = Request("""
        {"model":"remote","max_tokens":100,"stream":true,"stop_sequences":["STOP"],
         "system":[{"type":"text","text":"x-anthropic-billing-header: abc"},{"type":"text","text":"Be helpful","cache_control":{"type":"ephemeral"}}],
         "tools":[{"name":"read_file","description":"Read a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}],
         "tool_choice":{"type":"any","disable_parallel_tool_use":true},
         "messages":[{"role":"assistant","content":[{"type":"text","text":"Reading"},{"type":"tool_use","id":"call-1","name":"read_file","input":{"path":"a.txt"}}]},
         {"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":[{"type":"text","text":"contents"}]},{"type":"text","text":"Continue"}]}]}
        """);
        var root = doc.RootElement;
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("STOP", root.GetProperty("stop")[0].GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("Be helpful", messages[0].GetProperty("content").GetString());
        Assert.Equal("Reading", messages[1].GetProperty("content").GetString());
        var call = messages[1].GetProperty("tool_calls")[0];
        Assert.Equal("call-1", call.GetProperty("id").GetString());
        using var args = JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!);
        Assert.Equal("a.txt", args.RootElement.GetProperty("path").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call-1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("contents", messages[2].GetProperty("content").GetString());
        Assert.Equal("Continue", messages[3].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("none", "none")]
    public void ToolChoice_MapsModes(string type, string expected)
    {
        using var doc = Request("""{"model":"m","max_tokens":1,"tool_choice":{"type":"TYPE_PLACEHOLDER"},"messages":[{"role":"user","content":"hi"}]}""".Replace("TYPE_PLACEHOLDER", type));
        Assert.Equal(expected, doc.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public void NamedToolChoice_UsesFunctionObject()
    {
        using var doc = Request("""{"model":"m","max_tokens":1,"tool_choice":{"type":"tool","name":"read"},"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal("read", doc.RootElement.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void ImageSources_ConvertWithoutLosingBytes()
    {
        using var doc = Request("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"type":"text","text":"look"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGVsbG8="}},{"type":"image","source":{"type":"url","url":"https://example.test/a.png"}}]}]}""");
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("look", content[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,aGVsbG8=", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("https://example.test/a.png", content[2].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("{\"model\":\"m\",\"messages\":[]}")]
    [InlineData("{\"model\":\"m\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"document\"}]}]}")]
    [InlineData("{\"model\":\"m\",\"max_tokens\":1,\"tools\":[{\"type\":\"web_search_20250305\",\"name\":\"web_search\"}],\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}")]
    public void UnsupportedRequests_FailExplicitly(string json) => Assert.Throws<TranslationException>(() => Request(json));

    [Theory]
    [InlineData("stop", "end_turn")]
    [InlineData("length", "max_tokens")]
    [InlineData("content_filter", "refusal")]
    public void Responses_RestoreClientModelAndFinishReason(string finish, string expected)
    {
        using var doc = Response("""{"id":"abc","choices":[{"message":{"content":"remote-model is text","reasoning_content":"Thinking"},"finish_reason":"FINISH_PLACEHOLDER"}],"usage":{"prompt_tokens":20,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":5}}} """.Replace("FINISH_PLACEHOLDER", finish));
        var root = doc.RootElement;
        Assert.Equal("claude-client", root.GetProperty("model").GetString());
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("Thinking", root.GetProperty("content")[0].GetProperty("thinking").GetString());
        Assert.Equal("", root.GetProperty("content")[0].GetProperty("signature").GetString());
        Assert.Equal("remote-model is text", root.GetProperty("content")[1].GetProperty("text").GetString());
        Assert.Equal(expected, root.GetProperty("stop_reason").GetString());
        Assert.Equal(15, root.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, root.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32());
        Assert.Equal(4, root.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void ToolResponse_ParsesArgumentJson()
    {
        using var doc = Response("""{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call-a","type":"function","function":{"name":"read","arguments":"{\"path\":\"a.txt\"}"}}]},"finish_reason":"tool_calls"}]}""");
        Assert.Equal("tool_use", doc.RootElement.GetProperty("stop_reason").GetString());
        var tool = doc.RootElement.GetProperty("content")[0];
        Assert.Equal("call-a", tool.GetProperty("id").GetString());
        Assert.Equal("read", tool.GetProperty("name").GetString());
        Assert.Equal("a.txt", tool.GetProperty("input").GetProperty("path").GetString());
    }

    [Fact]
    public void ImageToolResults_KeepToolRepliesAdjacentAndLiftImagesIntoUserMessage()
    {
        using var doc = Request("""
        {"model":"m","max_tokens":1,"messages":[{"role":"user","content":[
          {"type":"tool_result","tool_use_id":"a","is_error":true,"content":[{"type":"text","text":"screenshot failed"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGVsbG8="}}]},
          {"type":"tool_result","tool_use_id":"b","content":"second result"},
          {"type":"text","text":"Continue"}]}]}
        """);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("tool", messages[0].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.String, messages[0].GetProperty("content").ValueKind);
        Assert.Contains("[Tool error]", messages[0].GetProperty("content").GetString());
        Assert.Equal("tool", messages[1].GetProperty("role").GetString());
        Assert.Equal("b", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Contains(messages[2].GetProperty("content").EnumerateArray(), part => part.GetProperty("type").GetString() == "image_url");
    }

    [Fact]
    public async Task ParallelToolNames_CanFinishAfterAnotherToolStartsArguments()
    {
        var events = await Stream("""
        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"read","arguments":""}},{"index":1,"id":"b","function":{"name":"wri","arguments":""}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"name":"te","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """);
        var starts = events.Where(e => e.GetProperty("type").GetString() == "content_block_start").ToArray();
        Assert.Equal("read", starts[0].GetProperty("content_block").GetProperty("name").GetString());
        Assert.Equal("write", starts[1].GetProperty("content_block").GetProperty("name").GetString());
    }

    [Fact]
    public async Task InBandRateLimit_UsesAnthropicRateLimitType()
    {
        var events = await Stream("data: {\"error\":{\"message\":\"Rate limited\",\"code\":429}}\n\n");
        Assert.Equal("rate_limit_error", events[^1].GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("Rate limited", events[^1].GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReasoningStream_ProducesCompleteThinkingBlockShapeBeforeText()
    {
        var events = await Stream("data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Think\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        Assert.Equal("thinking", events[1].GetProperty("content_block").GetProperty("type").GetString());
        Assert.Equal("", events[1].GetProperty("content_block").GetProperty("signature").GetString());
        Assert.Equal("thinking_delta", events[2].GetProperty("delta").GetProperty("type").GetString());
        Assert.Equal("content_block_stop", events[3].GetProperty("type").GetString());
        Assert.Equal("text", events[4].GetProperty("content_block").GetProperty("type").GetString());
        Assert.Equal(1, events[4].GetProperty("index").GetInt32());
    }

    [Fact]
    public void ConsecutiveUserMessages_KeepParallelToolRepliesBeforeImageFollowup()
    {
        using var doc = Request("""
        {"model":"m","max_tokens":1,"messages":[
          {"role":"assistant","content":[{"type":"tool_use","id":"a","name":"screenshot","input":{}},{"type":"tool_use","id":"b","name":"read","input":{}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"a","content":[{"type":"image","source":{"type":"url","url":"https://example.test/screen.png"}}]}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"b","content":"result"}]},
          {"role":"user","content":"Continue"}]}
        """);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("tool", messages[1].GetProperty("role").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("b", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Contains(messages[3].GetProperty("content").EnumerateArray(), p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString() == "Continue");
    }

    private static async Task<JsonElement[]> Stream(string sse)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await new AnthropicSseTranslator(ctx.Response, "claude-client", CancellationToken.None).RunAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return text.Split('\n').Where(l => l.StartsWith("data: ")).Select(l => JsonDocument.Parse(l[6..]).RootElement.Clone()).ToArray();
    }

    [Fact]
    public async Task StreamingText_EmitsLifecycleAndUsageFromTrailingChunk()
    {
        var events = await Stream("""
        : keepalive

        data: {"id":"s","choices":[{"index":0,"delta":{"content":"Hello "},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"content":"world"},"finish_reason":"stop"}]}

        data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2}}

        data: [DONE]

        """);
        Assert.Equal(new[] { "message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop" }, events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("claude-client", events[0].GetProperty("message").GetProperty("model").GetString());
        Assert.Equal("Hello ", events[2].GetProperty("delta").GetProperty("text").GetString());
        Assert.Equal("world", events[3].GetProperty("delta").GetProperty("text").GetString());
        Assert.Equal(10, events[^2].GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, events[^2].GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public async Task ParallelTools_KeepIndicesAndIncrementalArguments()
    {
        var events = await Stream("""
        data: {"id":"s","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-a","function":{"name":"read","arguments":""}},{"index":1,"id":"call-b","function":{"name":"write","arguments":""}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"b\":"}},{"index":0,"function":{"arguments":"{\"a\":"}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}},{"index":1,"function":{"arguments":"2}"}}]},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """);
        var starts = events.Where(e => e.GetProperty("type").GetString() == "content_block_start").ToArray();
        Assert.Equal(2, starts.Length);
        Assert.Equal("call-a", starts[0].GetProperty("content_block").GetProperty("id").GetString());
        Assert.Equal("call-b", starts[1].GetProperty("content_block").GetProperty("id").GetString());
        foreach (var (index, expected) in new[] { (0, "{\"a\":1}"), (1, "{\"b\":2}") })
        {
            var args = string.Concat(events.Where(e => e.GetProperty("type").GetString() == "content_block_delta" && e.GetProperty("index").GetInt32() == index).Select(e => e.GetProperty("delta").GetProperty("partial_json").GetString()));
            Assert.Equal(expected, args);
        }
        Assert.Equal("tool_use", events[^2].GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Equal(2, events.Count(e => e.GetProperty("type").GetString() == "content_block_stop"));
    }

    [Theory]
    [InlineData("data: {\"error\":{\"message\":\"Rate limited\",\"code\":429}}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n")]
    [InlineData("data: invalid\n\n")]
    [InlineData("data: [DONE]\n\n")]
    public async Task BrokenStreams_EmitErrorWithoutSuccessStop(string sse)
    {
        var events = await Stream(sse);
        Assert.Equal("error", events[^1].GetProperty("type").GetString());
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "message_stop");
    }
}
