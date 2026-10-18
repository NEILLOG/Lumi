using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

public class RoutingChatProviderTests
{
    private static RoutingChatProvider Create(FakeAnthropicHttpHandler handler) => new(
        new AnthropicChatProvider(new HttpClient(handler), TestSupport.FakeSecretStore.WithKey("sk-test"),
            Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }),
            new CapturingLogger<AnthropicChatProvider>()),
        new OpenAICompatibleChatProvider(new HttpClient(handler), TestSupport.FakeSecretStore.WithKey("sk-test"),
            new CapturingLogger<OpenAICompatibleChatProvider>()));

    private static ChatRequest Request(ProviderFormat format)
    {
        var connection = new ConnectionSettings("c1", "連線", format, "https://example.test/v1");
        var model = new ModelSettings("m1", "c1", "some-model", "某模型", 512, 8_000, false);
        return new ChatRequest(new ResolvedModel(model, connection),
            [new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("hi")], DateTimeOffset.UtcNow)]);
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact]
    public async Task AnthropicConnection_GoesToAnthropicProvider()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);

        var events = await Collect(Create(handler).StreamAsync(Request(ProviderFormat.Anthropic)));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task OpenAICompatibleConnection_GoesToOpenAIProvider()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        var events = await Collect(Create(handler).StreamAsync(Request(ProviderFormat.OpenAICompatible)));

        Assert.IsType<Completed>(events[^1]);
        Assert.EndsWith("/chat/completions", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SwitchingFormatsMidConversation_KeepsHistoryUsable_BothWays()
    {
        var claudeHandler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("thinking-with-signature"), 64);
        var openAiHandler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var router = new RoutingChatProvider(
            new AnthropicChatProvider(new HttpClient(claudeHandler), TestSupport.FakeSecretStore.WithKey("sk-a"),
                Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }), new CapturingLogger<AnthropicChatProvider>()),
            new OpenAICompatibleChatProvider(new HttpClient(openAiHandler), TestSupport.FakeSecretStore.WithKey("sk-o"),
                new CapturingLogger<OpenAICompatibleChatProvider>()));
        var history = new List<ChatMessage>
        {
            new(ChatMessage.NewId(), ChatRole.User, [new TextBlock("第一題")], DateTimeOffset.UtcNow),
        };

        async Task Turn(ProviderFormat format, string next)
        {
            var events = await Collect(router.StreamAsync(new ChatRequest(Request(format).Model, [.. history])));
            history.Add(Assert.IsType<Completed>(events[^1]).Message);
            history.Add(new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(next)], DateTimeOffset.UtcNow));
        }

        await Turn(ProviderFormat.Anthropic, "第二題");        // 產生 thinking + signature
        await Turn(ProviderFormat.OpenAICompatible, "第三題");  // 只收到文字
        await Turn(ProviderFormat.Anthropic, "第四題");        // 換回來：先前的 thinking 區塊與 signature 仍原樣送回

        using var openAiBody = System.Text.Json.JsonDocument.Parse(openAiHandler.LastRequestBody!);
        Assert.DoesNotContain("opaque-signature", openAiBody.RootElement.GetRawText());
        Assert.Equal(3, openAiBody.RootElement.GetProperty("messages").GetArrayLength());

        // 最後一次 Anthropic 請求：開頭的 Anthropic 回應（thinking + signature）還在
        Assert.Contains("opaque-signature-abc123==", claudeHandler.LastRequestBody);
        Assert.Equal(1, openAiHandler.CallCount);
        Assert.Equal(2, claudeHandler.CallCount);
    }
}
