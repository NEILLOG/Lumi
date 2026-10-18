using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

public class RoutingChatProviderTests
{
    private static RoutingChatProvider Create(FakeAnthropicHttpHandler handler) => new(
        new AnthropicChatProvider(new HttpClient(handler), TestSupport.FakeSecretStore.WithKey("sk-test"),
            Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }),
            new CapturingLogger<AnthropicChatProvider>()));

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
    public async Task OpenAICompatible_IsNotSupportedYet_AndMakesNoHttpCall()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);

        var events = await Collect(Create(handler).StreamAsync(Request(ProviderFormat.OpenAICompatible)));

        Assert.IsType<Failed>(Assert.Single(events));
        Assert.Equal(0, handler.CallCount);
    }
}
