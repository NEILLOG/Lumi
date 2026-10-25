using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

public class AnthropicToolTests
{
    private static readonly ConnectionSettings Connection =
        new("test-anthropic", "測試連線", ProviderFormat.Anthropic, "https://api.anthropic.test");

    private static readonly ResolvedModel Model =
        new(new ModelSettings("m1", Connection.Id, "claude-opus-5-5", "Claude Opus 5.5", 1024, 1_000_000, true), Connection);

    private static AnthropicChatProvider Create(FakeAnthropicHttpHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") },
        FakeSecretStore.WithKey("sk-ant-test-FAKE-KEY-0000"),
        Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }),
        new CapturingLogger<AnthropicChatProvider>());

    private static ChatMessage Msg(ChatRole role, params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UtcNow);

    private static ChatRequest Request(IReadOnlyList<ChatMessage>? history = null, bool withTools = true) => new(
        Model, history ?? [Msg(ChatRole.User, new TextBlock("嗨"))], withTools ? [Lumi.Core.Tools.ToolDefinition.From(new EchoTool())] : null);

    private static async Task<List<ChatEvent>> Collect(AnthropicChatProvider provider, ChatRequest request)
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
    public async Task ToolUse_ArgumentsAccumulateAcrossChunks_IncludingChineseAndEmoji(int chunkSize)
    {
        var provider = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("tool-use"), chunkSize));

        var events = await Collect(provider, Request());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Single(events.OfType<Completed>());
        Assert.Equal(StopReason.ToolUse, completed.Reason);
        Assert.Equal("我來幫你處理。", Assert.IsType<TextBlock>(completed.Message.Content[0]).Text);
        var use = Assert.IsType<ToolUseBlock>(completed.Message.Content[1]);
        Assert.Equal("toolu_01A", use.Id);
        Assert.Equal("echo", use.Name);
        using var doc = JsonDocument.Parse(use.InputJson);
        Assert.Equal("你好，世界 ❤️‍🔥", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("times").GetInt32());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task ParallelToolUses_WithThinking_KeepOrderSignatureAndEmptyArguments(int chunkSize)
    {
        var provider = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("tool-use-parallel-thinking"), chunkSize));

        var events = await Collect(provider, Request());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal(StopReason.ToolUse, completed.Reason);
        Assert.Equal(3, completed.Message.Content.Count);
        var thinking = Assert.IsType<ThinkingBlock>(completed.Message.Content[0]);
        Assert.Equal("sig-tool-xyz==", thinking.Signature);
        var time = Assert.IsType<ToolUseBlock>(completed.Message.Content[1]);
        Assert.Equal(("toolu_01B", "get_current_time", "{}"), (time.Id, time.Name, time.InputJson));   // 沒有引數 → {}
        var calc = Assert.IsType<ToolUseBlock>(completed.Message.Content[2]);
        Assert.Equal("""{"expression": "27 * 453"}""", calc.InputJson);
    }

    [Fact]
    public async Task TruncatedToolUse_IsDropped_AndReasonIsMaxTokens()
    {
        var provider = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("tool-use-truncated"), 5));

        var events = await Collect(provider, Request());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal(StopReason.MaxTokens, completed.Reason);
        Assert.Equal("準備呼叫", Assert.IsType<TextBlock>(Assert.Single(completed.Message.Content)).Text);
    }

    // ---- 請求 ----

    [Fact]
    public async Task Request_SendsToolDefinitions_WithFullSchema()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);

        await Collect(Create(handler), Request());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var tool = Assert.Single(doc.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("echo", tool.GetProperty("name").GetString());
        Assert.Equal("把文字回聲回來。", tool.GetProperty("description").GetString());
        var schema = tool.GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("要回聲的文字", schema.GetProperty("properties").GetProperty("text").GetProperty("description").GetString());
        Assert.Equal(["text"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Request_WithoutTools_HasNoToolsField()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);

        await Collect(Create(handler), Request(withTools: false));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task History_ToolUseAndResults_AreConvertedInOrder()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var history = new[]
        {
            Msg(ChatRole.User, new TextBlock("請回聲")),
            Msg(ChatRole.Assistant, new ThinkingBlock("想", "sig-1"), new TextBlock("好"),
                new ToolUseBlock("toolu_1", "echo", """{"text":"你好","times":2}"""), new ToolUseBlock("toolu_2", "echo", "{}")),
            Msg(ChatRole.Tool, Result("toolu_1", "你好你好"), Result("toolu_2", "參數不正確", isError: true)),
            Msg(ChatRole.Assistant, new TextBlock("完成")),
            Msg(ChatRole.User, new TextBlock("謝謝")),
        };

        await Collect(Create(handler), Request(history));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(["user", "assistant", "user", "assistant", "user"],
            messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        var assistant = messages[1].GetProperty("content");
        Assert.Equal(["thinking", "text", "tool_use", "tool_use"], assistant.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("sig-1", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("toolu_1", assistant[2].GetProperty("id").GetString());
        Assert.Equal("你好", assistant[2].GetProperty("input").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Object, assistant[3].GetProperty("input").ValueKind);

        var results = messages[2].GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("tool_result", results[0].GetProperty("type").GetString());
        Assert.Equal("toolu_1", results[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("你好你好", results[0].GetProperty("content").GetString());
        Assert.False(results[0].TryGetProperty("is_error", out var flag) && flag.GetBoolean());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task History_UserMessageAfterToolResults_IsMergedWithResultsFirst()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var history = new[]
        {
            Msg(ChatRole.User, new TextBlock("請回聲")),
            Msg(ChatRole.Assistant, new ToolUseBlock("toolu_1", "echo", """{"text":"a"}""")),
            Msg(ChatRole.Tool, Result("toolu_1", "a")),
            Msg(ChatRole.User, new TextBlock("算了，換個問題")),
        };

        await Collect(Create(handler), Request(history));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var last = messages[2].GetProperty("content");
        Assert.Equal(["tool_result", "text"], last.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task History_InvalidToolInputJson_IsSentAsEmptyObject()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var history = new[]
        {
            Msg(ChatRole.User, new TextBlock("x")),
            Msg(ChatRole.Assistant, new ToolUseBlock("toolu_1", "echo", "{壞掉")),
            Msg(ChatRole.Tool, Result("toolu_1", "參數不正確", isError: true)),
        };

        await Collect(Create(handler), Request(history));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var input = doc.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("input");
        Assert.Equal(JsonValueKind.Object, input.ValueKind);
        Assert.Empty(input.EnumerateObject());
    }
}
