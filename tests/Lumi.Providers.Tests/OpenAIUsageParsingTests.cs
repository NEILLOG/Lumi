using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;

namespace Lumi.Providers.Tests;

/// <summary>串流最後一個 chunk 的 usage 解析：快取命中數從哪個欄位讀（OpenAI 與 DeepSeek 寫法不同）。</summary>
public class OpenAIUsageParsingTests
{
    private static readonly ConnectionSettings Connection =
        new("test-openai", "測試 OpenAI", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static ResolvedModel Model() =>
        new(new ModelSettings("m1", Connection.Id, "gpt-test", "GPT Test", 2048, 128_000, true), Connection);

    private static ChatRequest Request() => new(Model(),
        [new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("Hi")], DateTimeOffset.UtcNow)]);

    // 只有一段文字與一個帶 usage 的 chunk；usage 的 JSON 由測試指定
    private static byte[] SseWithUsage(string usageJson) => Encoding.UTF8.GetBytes(
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-test\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"好\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-test\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-test\",\"choices\":[],\"usage\":" + usageJson + "}\n\n" +
        "data: [DONE]\n\n");

    private static async Task<TokenUsage?> UsageOf(string usageJson)
    {
        var provider = new OpenAICompatibleChatProvider(
            new HttpClient(FakeAnthropicHttpHandler.ForSse(SseWithUsage(usageJson), 16)),
            FakeSecretStore.WithKey("sk-openai-FAKE-KEY-1234"),
            new CapturingLogger<OpenAICompatibleChatProvider>());

        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(Request(), CancellationToken.None))
            events.Add(e);
        return Assert.IsType<Completed>(events[^1]).Message.Usage;
    }

    [Fact]
    public async Task OpenAI_CachedTokensInDetails_AreUsedAsCacheRead()
    {
        // prompt_tokens 含快取：輸入 = 25 - 10
        var usage = await UsageOf("{\"prompt_tokens\":25,\"completion_tokens\":6,\"total_tokens\":31,\"prompt_tokens_details\":{\"cached_tokens\":10}}");

        Assert.Equal(new TokenUsage(15, 6, 0, 10), usage);
    }

    [Fact]
    public async Task DeepSeek_PromptCacheHitTokens_AreUsedWhenNoDetails()
    {
        var usage = await UsageOf("{\"prompt_tokens\":100,\"completion_tokens\":5,\"total_tokens\":105,\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":20}");

        Assert.Equal(new TokenUsage(20, 5, 0, 80), usage);
    }

    [Fact]
    public async Task DetailsWithoutCachedTokens_FallBackToDeepSeekField()
    {
        var usage = await UsageOf("{\"prompt_tokens\":100,\"completion_tokens\":5,\"prompt_tokens_details\":{},\"prompt_cache_hit_tokens\":80}");

        Assert.Equal(new TokenUsage(20, 5, 0, 80), usage);
    }

    [Fact]
    public async Task CachedTokensInDetails_WinOverDeepSeekField_WhenBothPresent()
    {
        var usage = await UsageOf("{\"prompt_tokens\":100,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":10},\"prompt_cache_hit_tokens\":80}");

        Assert.Equal(new TokenUsage(90, 5, 0, 10), usage);
    }

    [Fact]
    public async Task NoCacheFields_MeansZeroCacheRead()
    {
        var usage = await UsageOf("{\"prompt_tokens\":25,\"completion_tokens\":6,\"total_tokens\":31}");

        Assert.Equal(new TokenUsage(25, 6, 0, 0), usage);
    }
}
