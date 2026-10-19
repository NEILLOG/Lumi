using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

/// <summary>「模型呼叫工具 → 回填結果 → 模型回答」的兩次請求，以及同一段歷史在兩種格式之間切換。</summary>
public class ToolRoundTripTests
{
    private static readonly ConnectionSettings AnthropicConnection =
        new("c-anthropic", "A", ProviderFormat.Anthropic, "https://api.anthropic.test");
    private static readonly ConnectionSettings OpenAiConnection =
        new("c-openai", "O", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static ResolvedModel AnthropicModel => new(new ModelSettings("m1", AnthropicConnection.Id, "claude-opus-5-5", "C", 1024, 1_000_000, true), AnthropicConnection);
    private static ResolvedModel OpenAiModel => new(new ModelSettings("m2", OpenAiConnection.Id, "gpt-test", "G", 1024, 128_000, true), OpenAiConnection);

    private static AnthropicChatProvider Anthropic(FakeAnthropicHttpHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") }, FakeSecretStore.WithKey("sk-ant-test-FAKE-KEY-0000"),
        Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }), new CapturingLogger<AnthropicChatProvider>());

    private static OpenAICompatibleChatProvider OpenAi(FakeAnthropicHttpHandler handler) =>
        new(new HttpClient(handler), FakeSecretStore.WithKey("sk-openai-FAKE-KEY-1234"), new CapturingLogger<OpenAICompatibleChatProvider>());

    private static ChatMessage Msg(ChatRole role, params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UtcNow);

    private static async Task<List<ChatEvent>> Collect(IChatProvider provider, ChatRequest request)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(request))
            events.Add(e);
        return events;
    }

    private static ToolResultBlock ResultFor(ToolUseBlock use) =>
        new(use.Id, "結果：" + use.Name, false, use.Name, null, null, 1, ToolApproval.NotRequired);

    private static readonly IReadOnlyList<Lumi.Core.Tools.ITool> Tools = [new EchoTool()];

    [Fact]
    public async Task Anthropic_TwoRequests_SecondCarriesToolUseAndResult()
    {
        var handler = FakeAnthropicHttpHandler.ForSseSequence(5, Fixtures.Sse("tool-use-parallel-thinking"), Fixtures.Sse("basic-text"));
        var provider = Anthropic(handler);
        var history = new List<ChatMessage> { Msg(ChatRole.User, new TextBlock("算一下並告訴我時間")) };

        var first = Assert.IsType<Completed>((await Collect(provider, new ChatRequest(AnthropicModel, history, Tools)))[^1]);
        history.Add(first.Message);
        history.Add(Msg(ChatRole.Tool, first.Message.Content.OfType<ToolUseBlock>().Select(ResultFor).ToArray()));
        var second = await Collect(provider, new ChatRequest(AnthropicModel, history, Tools));

        Assert.Equal(StopReason.EndTurn, Assert.IsType<Completed>(second[^1]).Reason);
        Assert.Equal(2, handler.AllRequestBodies.Count);
        using var doc = JsonDocument.Parse(handler.AllRequestBodies[1]);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(["user", "assistant", "user"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(["thinking", "tool_use", "tool_use"],
            messages[1].GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("sig-tool-xyz==", messages[1].GetProperty("content")[0].GetProperty("signature").GetString());   // 工具呼叫那則助理訊息帶著原本的 thinking 與簽章
        var results = messages[2].GetProperty("content");
        Assert.Equal(["toolu_01B", "toolu_01C"], results.EnumerateArray().Select(r => r.GetProperty("tool_use_id").GetString()));
    }

    [Fact]
    public async Task OpenAi_TwoRequests_SecondCarriesToolCallsAndToolMessages()
    {
        var handler = FakeAnthropicHttpHandler.ForSseSequence(5, Fixtures.OpenAISse("tool-calls"), Fixtures.OpenAISse("basic-text"));
        var provider = OpenAi(handler);
        var history = new List<ChatMessage> { Msg(ChatRole.User, new TextBlock("請回聲並告訴我時間")) };

        var first = Assert.IsType<Completed>((await Collect(provider, new ChatRequest(OpenAiModel, history, Tools)))[^1]);
        history.Add(first.Message);
        history.Add(Msg(ChatRole.Tool, first.Message.Content.OfType<ToolUseBlock>().Select(ResultFor).ToArray()));
        var second = await Collect(provider, new ChatRequest(OpenAiModel, history, Tools));

        Assert.IsType<Completed>(second[^1]);
        using var doc = JsonDocument.Parse(handler.AllRequestBodies[1]);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(["user", "assistant", "tool", "tool"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("我來處理。", messages[1].GetProperty("content").GetString());
        Assert.Equal(["call_aaa", "call_bbb"], messages[1].GetProperty("tool_calls").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.Equal(["call_aaa", "call_bbb"], new[] { messages[2], messages[3] }.Select(m => m.GetProperty("tool_call_id").GetString()));
    }

    [Fact]
    public async Task AnthropicHistory_ContinuesOnOpenAi_ThinkingIsDropped()
    {
        // 先用 Anthropic 取得一則含 thinking 與工具呼叫的助理訊息，再把同一段歷史送給 OpenAI 相容的伺服器
        var anthropic = Anthropic(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("tool-use-parallel-thinking"), 16));
        var assistant = Assert.IsType<Completed>((await Collect(anthropic, new ChatRequest(AnthropicModel, [Msg(ChatRole.User, new TextBlock("x"))], Tools)))[^1]).Message;
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var history = new[]
        {
            Msg(ChatRole.User, new TextBlock("x")),
            assistant,
            Msg(ChatRole.Tool, assistant.Content.OfType<ToolUseBlock>().Select(ResultFor).ToArray()),
        };

        var events = await Collect(OpenAi(handler), new ChatRequest(OpenAiModel, history, Tools));

        Assert.IsType<Completed>(events[^1]);
        Assert.DoesNotContain("sig-tool-xyz", handler.LastRequestBody!);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(["user", "assistant", "tool", "tool"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(["toolu_01B", "toolu_01C"], messages[1].GetProperty("tool_calls").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task OpenAiHistory_ContinuesOnAnthropic()
    {
        var openAi = OpenAi(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("tool-calls"), 16));
        var assistant = Assert.IsType<Completed>((await Collect(openAi, new ChatRequest(OpenAiModel, [Msg(ChatRole.User, new TextBlock("x"))], Tools)))[^1]).Message;
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var history = new[]
        {
            Msg(ChatRole.User, new TextBlock("x")),
            assistant,
            Msg(ChatRole.Tool, assistant.Content.OfType<ToolUseBlock>().Select(ResultFor).ToArray()),
        };

        var events = await Collect(Anthropic(handler), new ChatRequest(AnthropicModel, history, Tools));

        Assert.IsType<Completed>(events[^1]);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(["user", "assistant", "user"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        var uses = messages[1].GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "tool_use").ToList();
        Assert.Equal(["call_aaa", "call_bbb"], uses.Select(u => u.GetProperty("id").GetString()));
        Assert.Equal("你好，世界 ❤️‍🔥", uses[0].GetProperty("input").GetProperty("text").GetString());
        Assert.Equal(2, messages[2].GetProperty("content").GetArrayLength());
    }
}
