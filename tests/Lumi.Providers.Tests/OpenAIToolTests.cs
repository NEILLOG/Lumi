using System.Net;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;

namespace Lumi.Providers.Tests;

public class OpenAIToolTests
{
    private static readonly ConnectionSettings Connection =
        new("test-openai", "測試 OpenAI", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static readonly ResolvedModel Model =
        new(new ModelSettings("m1", Connection.Id, "gpt-test", "GPT Test", 2048, 128_000, true), Connection);

    private static OpenAICompatibleChatProvider Create(FakeAnthropicHttpHandler handler) =>
        new(new HttpClient(handler), FakeSecretStore.WithKey("sk-openai-FAKE-KEY-1234"), new CapturingLogger<OpenAICompatibleChatProvider>());

    private static ChatMessage Msg(ChatRole role, params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UtcNow);

    private static ChatRequest Request(IReadOnlyList<ChatMessage>? history = null, bool withTools = true) => new(
        Model, history ?? [Msg(ChatRole.User, new TextBlock("嗨"))], withTools ? [Lumi.Core.Tools.ToolDefinition.From(new EchoTool())] : null);

    private static async Task<List<ChatEvent>> Collect(OpenAICompatibleChatProvider provider, ChatRequest request)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(request))
            events.Add(e);
        return events;
    }

    private static ToolResultBlock Result(string id, string content, bool isError = false) =>
        new(id, content, isError, "回聲", null, null, 1, ToolApproval.NotRequired);

    // ---- 串流 ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task ToolCalls_AccumulateByIndex_TextFirst_AcrossChunks(int chunkSize)
    {
        var provider = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("tool-calls"), chunkSize));

        var events = await Collect(provider, Request());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Single(events.OfType<Completed>());
        Assert.Equal(StopReason.ToolUse, completed.Reason);
        Assert.Equal(3, completed.Message.Content.Count);
        Assert.Equal("我來處理。", Assert.IsType<TextBlock>(completed.Message.Content[0]).Text);
        var first = Assert.IsType<ToolUseBlock>(completed.Message.Content[1]);
        Assert.Equal(("call_aaa", "echo"), (first.Id, first.Name));
        using var doc = JsonDocument.Parse(first.InputJson);
        Assert.Equal("你好，世界 ❤️‍🔥", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("times").GetInt32());
        var second = Assert.IsType<ToolUseBlock>(completed.Message.Content[2]);
        Assert.Equal(("call_bbb", "get_current_time", "{}"), (second.Id, second.Name, second.InputJson));
        Assert.Equal(new TokenUsage(40, 20, 0, 0), completed.Message.Usage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task OllamaStyle_SingleChunk_NoIdNoIndex_StopFinishReason_StillToolUse(int chunkSize)
    {
        var provider = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("tool-calls-ollama-style"), chunkSize));

        var events = await Collect(provider, Request());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal(StopReason.ToolUse, completed.Reason);   // finish_reason 是 stop，但有工具呼叫：以內容為準
        var use = Assert.IsType<ToolUseBlock>(Assert.Single(completed.Message.Content));
        Assert.Equal("get_current_time", use.Name);
        Assert.Equal("{}", use.InputJson);
        Assert.StartsWith("call_", use.Id);   // 伺服器沒給 id：自己產生，回填結果時用同一個
        Assert.True(use.Id.Length > 10);
    }

    [Fact]
    public async Task MissingIndex_ContinuationChunksAppendToLastCall()
    {
        var provider = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("tool-calls-missing-index"), 4));

        var events = await Collect(provider, Request());

        var completed = Assert.IsType<Completed>(events[^1]);
        var use = Assert.IsType<ToolUseBlock>(Assert.Single(completed.Message.Content));
        Assert.Equal(("call_ccc", """{"text":"abc"}"""), (use.Id, use.InputJson));
        Assert.Equal(StopReason.ToolUse, completed.Reason);
    }

    [Fact]
    public async Task TruncatedArguments_AreDropped_AndReasonIsMaxTokens()
    {
        var provider = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("tool-calls-truncated"), 8));

        var events = await Collect(provider, Request());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal(StopReason.MaxTokens, completed.Reason);
        Assert.Empty(completed.Message.Content);
    }

    // ---- 請求 ----

    [Fact]
    public async Task Request_SendsToolDefinitions()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        await Collect(Create(handler), Request());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var tool = Assert.Single(doc.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var function = tool.GetProperty("function");
        Assert.Equal("echo", function.GetProperty("name").GetString());
        Assert.Equal("把文字回聲回來。", function.GetProperty("description").GetString());
        var parameters = function.GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal("要回聲的文字", parameters.GetProperty("properties").GetProperty("text").GetProperty("description").GetString());
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Request_WithoutTools_HasNoToolsField()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        await Collect(Create(handler), Request(withTools: false));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task History_ToolCallsAndResults_AreConverted()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var history = new[]
        {
            Msg(ChatRole.User, new TextBlock("請回聲")),
            Msg(ChatRole.Assistant, new ThinkingBlock("想", "sig"), new ToolUseBlock("call_1", "echo", """{"text":"你好"}"""), new ToolUseBlock("call_2", "echo", "")),
            Msg(ChatRole.Tool, Result("call_1", "你好"), Result("call_2", "參數不正確", isError: true)),
            Msg(ChatRole.Assistant, new TextBlock("完成")),
            Msg(ChatRole.User, new TextBlock("謝謝")),
        };

        await Collect(Create(handler), Request(history));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(["user", "assistant", "tool", "tool", "assistant", "user"],
            messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        var assistant = messages[1];
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);   // 只有工具呼叫、沒有文字
        var calls = assistant.GetProperty("tool_calls");
        Assert.Equal("call_1", calls[0].GetProperty("id").GetString());
        Assert.Equal("function", calls[0].GetProperty("type").GetString());
        Assert.Equal("echo", calls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"text":"你好"}""", calls[0].GetProperty("function").GetProperty("arguments").GetString());   // 引數是字串
        Assert.Equal("{}", calls[1].GetProperty("function").GetProperty("arguments").GetString());                  // 空引數補 {}

        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("你好", messages[2].GetProperty("content").GetString());
        Assert.Equal("call_2", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("參數不正確", messages[3].GetProperty("content").GetString());
        Assert.DoesNotContain("sig", handler.LastRequestBody!);   // thinking 不送
    }

    [Fact]
    public async Task History_AssistantWithTextAndToolCall_KeepsBoth()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var history = new[]
        {
            Msg(ChatRole.User, new TextBlock("x")),
            Msg(ChatRole.Assistant, new TextBlock("我來查"), new ToolUseBlock("call_1", "echo", "{}")),
            Msg(ChatRole.Tool, Result("call_1", "ok")),
        };

        await Collect(Create(handler), Request(history));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var assistant = doc.RootElement.GetProperty("messages")[1];
        Assert.Equal("我來查", assistant.GetProperty("content").GetString());
        Assert.Equal(1, assistant.GetProperty("tool_calls").GetArrayLength());
    }

    // ---- 400 提示 ----

    [Fact]
    public async Task BadRequest_WithTools_HintsAtDisablingTools()
    {
        var provider = Create(FakeAnthropicHttpHandler.ForError(HttpStatusCode.BadRequest, Fixtures.OpenAIHttpError("400")));

        var events = await Collect(provider, Request());

        var failed = Assert.IsType<Failed>(events[^1]);
        Assert.Equal(ChatFailureKind.BadRequest, failed.Failure.Kind);
        Assert.Contains("不支援工具", failed.Failure.Message);
    }

    [Fact]
    public async Task BadRequest_WithoutTools_HasNoToolsHint()
    {
        var provider = Create(FakeAnthropicHttpHandler.ForError(HttpStatusCode.BadRequest, Fixtures.OpenAIHttpError("400")));

        var events = await Collect(provider, Request(withTools: false));

        Assert.DoesNotContain("工具", Assert.IsType<Failed>(events[^1]).Failure.Message);
    }

    [Fact]
    public async Task Unauthorized_WithTools_HasNoToolsHint()
    {
        var provider = Create(FakeAnthropicHttpHandler.ForError(HttpStatusCode.Unauthorized, Fixtures.OpenAIHttpError("401")));

        var events = await Collect(provider, Request());

        Assert.DoesNotContain("工具", Assert.IsType<Failed>(events[^1]).Failure.Message);
    }
}
