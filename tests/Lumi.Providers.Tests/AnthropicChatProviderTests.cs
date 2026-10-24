using System.Net;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

public class AnthropicChatProviderTests
{
    private const string FakeApiKey = "sk-ant-test-FAKE-KEY-0000";

    private static (AnthropicChatProvider Provider, FakeAnthropicHttpHandler Handler, CapturingLogger<AnthropicChatProvider> Logger) CreateProvider(
        FakeAnthropicHttpHandler handler, TestSupport.FakeSecretStore? secrets = null, AnthropicProviderOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") };
        var logger = new CapturingLogger<AnthropicChatProvider>();
        var opts = Options.Create(options ?? new AnthropicProviderOptions { UseServerFallbacks = false });
        var store = secrets ?? TestSupport.FakeSecretStore.WithKey(FakeApiKey);
        return (new AnthropicChatProvider(httpClient, store, opts, logger), handler, logger);
    }

    private static readonly ConnectionSettings Connection =
        new("test-anthropic", "測試連線", ProviderFormat.Anthropic, "https://api.anthropic.test");

    private static ResolvedModel Model(bool showThinking = true) =>
        new(new ModelSettings("m1", Connection.Id, "claude-opus-5-5", "Claude Opus 5.5", 1024, 1_000_000, showThinking), Connection);

    private static ChatRequest SimpleRequest(string userText = "Hi", bool showThinking = true) => new(
        Model(showThinking),
        [new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(userText)], DateTimeOffset.UtcNow)]);

    private static async Task<List<ChatEvent>> CollectAsync(AnthropicChatProvider provider, ChatRequest request, CancellationToken ct = default)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(request, ct))
            events.Add(e);
        return events;
    }

    private static string AllText(IEnumerable<ChatEvent> events) =>
        string.Concat(events.OfType<TextDelta>().Select(e => e.Text));

    // ---- 事件順序的約定：每個情境都要符合 ----

    private static void AssertEndsWithExactlyOneTerminalEvent(List<ChatEvent> events)
    {
        var terminalIndexes = events
            .Select((e, i) => (e, i))
            .Where(x => x.e is Completed or Failed)
            .Select(x => x.i)
            .ToList();

        Assert.Single(terminalIndexes);
        Assert.Equal(events.Count - 1, terminalIndexes[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task BasicText_ProducesTextDeltasThenCompleted(int chunkSize)
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), chunkSize));

        var events = await CollectAsync(provider, SimpleRequest());

        Assert.Equal("Hello, world!", AllText(events));
        AssertEndsWithExactlyOneTerminalEvent(events);
        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal(StopReason.EndTurn, completed.Reason);
        Assert.Equal(MessageStatus.Complete, completed.Message.Status);
        var text = Assert.Single(completed.Message.Content.OfType<TextBlock>());
        Assert.Equal("Hello, world!", text.Text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task UnicodeAndCombinedEmoji_SurviveChunking(int chunkSize)
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("unicode-emoji"), chunkSize));

        var events = await CollectAsync(provider, SimpleRequest());

        const string expected = "中文與組合 emoji：❤️‍🔥 和國旗 🇹🇼 完整無缺。";
        Assert.Equal(expected, AllText(events));
        var completed = Assert.IsType<Completed>(events[^1]);
        var text = Assert.Single(completed.Message.Content.OfType<TextBlock>());
        Assert.Equal(expected, text.Text);
    }

    [Fact]
    public async Task ThinkingBlock_AccumulatesTextAndSignature()
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("thinking-with-signature"), 5));

        var events = await CollectAsync(provider, SimpleRequest());

        var thinkingText = string.Concat(events.OfType<ThinkingDelta>().Select(e => e.Text));
        Assert.Equal("先想一下 27 * 453。 分解成 27 * 450 + 27 * 3。", thinkingText);

        var completed = Assert.IsType<Completed>(events[^1]);
        var thinking = Assert.Single(completed.Message.Content.OfType<ThinkingBlock>());
        Assert.Equal("先想一下 27 * 453。 分解成 27 * 450 + 27 * 3。", thinking.Thinking);
        Assert.Equal("opaque-signature-abc123==", thinking.Signature);
        var text = Assert.Single(completed.Message.Content.OfType<TextBlock>());
        Assert.Equal("答案是 12231。", text.Text);
    }

    [Fact]
    public async Task ThinkingBlock_SignatureIsSentBackUnchanged()
    {
        // 先跑一次拿到完整訊息（含 thinking + signature）
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("thinking-with-signature"), 64));
        var events = await CollectAsync(provider, SimpleRequest());
        var assistantMessage = Assert.IsType<Completed>(events[^1]).Message;

        // 把它當成歷史，组第二次請求，檢查送出的 JSON 裡 signature 原封不動
        var secondHandler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider2, handler2, _) = CreateProvider(secondHandler);
        var history = new ChatRequest(Model(),
            [
                new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("解 27*453")], DateTimeOffset.UtcNow),
                assistantMessage,
                new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("謝謝")], DateTimeOffset.UtcNow),
            ]);

        await CollectAsync(provider2, history);

        Assert.NotNull(handler2.LastRequestBody);
        // 中文會被 System.Text.Json 預設編碼成 \uXXXX，所以不能直接比對原始中文子字串；
        // 用 JsonDocument 解析後比對真正的值才準確。signature 是 ASCII，可以直接比對原始字串。
        Assert.Contains("opaque-signature-abc123==", handler2.LastRequestBody);

        using var doc = System.Text.Json.JsonDocument.Parse(handler2.LastRequestBody);
        var secondMessageContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
        var thinkingBlock = secondMessageContent.EnumerateArray().First(b => b.GetProperty("type").GetString() == "thinking");
        Assert.Equal("先想一下 27 * 453。 分解成 27 * 450 + 27 * 3。", thinkingBlock.GetProperty("thinking").GetString());
        Assert.Equal("opaque-signature-abc123==", thinkingBlock.GetProperty("signature").GetString());
    }

    [Theory]
    [InlineData("ping-interspersed", "有 ping 夾在中間也不受影響。")]
    [InlineData("unknown-event", "API 以後可能新增事件類型，不能因此失敗。")]
    public async Task NonFatalEventsDoNotBreakTheStream(string fixtureName, string expectedText)
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse(fixtureName), 64));

        var events = await CollectAsync(provider, SimpleRequest());

        Assert.Equal(expectedText, AllText(events));
        AssertEndsWithExactlyOneTerminalEvent(events);
        Assert.IsType<Completed>(events[^1]);
    }

    [Fact]
    public async Task StopReason_MaxTokens_IsMapped()
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("stop-max-tokens"), 64));

        var events = await CollectAsync(provider, SimpleRequest());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal(StopReason.MaxTokens, completed.Reason);
    }

    [Fact]
    public async Task Refusal_BeforeAnyOutput_ProducesFailedRefusedWithNoCompleted()
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("refusal-immediate"), 64));

        var events = await CollectAsync(provider, SimpleRequest());

        Assert.Empty(events.OfType<TextDelta>());
        Assert.DoesNotContain(events, e => e is Completed);
        var failed = Assert.IsType<Failed>(events[^1]);
        Assert.Equal(ChatFailureKind.Refused, failed.Failure.Kind);
        AssertEndsWithExactlyOneTerminalEvent(events);
    }

    [Fact]
    public async Task Refusal_MidStream_DiscardsPartialContent()
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("refusal-mid-stream"), 64));

        var events = await CollectAsync(provider, SimpleRequest());

        // 文字曾經即時串出過（UI 當下看得到），但最終結果是拒絕，不產生 Completed，不能存檔
        Assert.Equal("好的，我來", AllText(events));
        Assert.DoesNotContain(events, e => e is Completed);
        var failed = Assert.IsType<Failed>(events[^1]);
        Assert.Equal(ChatFailureKind.Refused, failed.Failure.Kind);
    }

    [Fact]
    public async Task FallbackBlock_IsPreservedAsProviderBlock_AndDoesNotBreakTheMessage()
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("fallback-block"), 64));

        var events = await CollectAsync(provider, SimpleRequest());

        var completed = Assert.IsType<Completed>(events[^1]);
        var providerBlock = Assert.Single(completed.Message.Content.OfType<ProviderBlock>());
        Assert.Equal("anthropic", providerBlock.Provider);
        var text = Assert.Single(completed.Message.Content.OfType<TextBlock>());
        Assert.Equal("由候補模型接手回答。", text.Text);
        // message_start 的 model 是候補模型，確認切換後的模型有被記下來
        Assert.Equal("claude-opus-4-8", completed.Message.ModelId);
    }

    [Fact]
    public async Task MissingApiKey_ProducesFailed_WithoutAnyHttpCall()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider, _, _) = CreateProvider(handler, TestSupport.FakeSecretStore.Empty());

        var events = await CollectAsync(provider, SimpleRequest());

        var failed = Assert.IsType<Failed>(Assert.Single(events));
        Assert.Equal(ChatFailureKind.MissingApiKey, failed.Failure.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SecretUnreadable_ProducesFailed_WithoutAnyHttpCall()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var ex = new Lumi.Core.Secrets.SecretStoreException("讀不出來");
        var (provider, _, logger) = CreateProvider(handler, TestSupport.FakeSecretStore.Throwing(ex));

        var events = await CollectAsync(provider, SimpleRequest());

        var failed = Assert.IsType<Failed>(Assert.Single(events));
        Assert.Equal(ChatFailureKind.SecretUnreadable, failed.Failure.Kind);
        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(FakeApiKey));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "400", ChatFailureKind.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, "401", ChatFailureKind.InvalidApiKey, false)]
    [InlineData(HttpStatusCode.NotFound, "404", ChatFailureKind.ModelNotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, "429", ChatFailureKind.RateLimited, true)]
    public async Task HttpErrors_AreMappedToChatFailure(HttpStatusCode status, string fixtureName, ChatFailureKind expectedKind, bool expectedRetryable)
    {
        var handler = FakeAnthropicHttpHandler.ForError(status, Fixtures.HttpError(fixtureName));
        var (provider, _, _) = CreateProvider(handler);

        var events = await CollectAsync(provider, SimpleRequest());

        var failed = Assert.IsType<Failed>(Assert.Single(events));
        Assert.Equal(expectedKind, failed.Failure.Kind);
        Assert.Equal(expectedRetryable, failed.Failure.Retryable);
    }

    [Fact]
    public async Task HttpErrors_LogTheServerMessage_ButNotTheKey()
    {
        var handler = FakeAnthropicHttpHandler.ForError(HttpStatusCode.BadRequest, Fixtures.HttpError("400"));
        var (provider, _, logger) = CreateProvider(handler);

        await CollectAsync(provider, SimpleRequest());

        Assert.Contains(logger.Messages, m => m.Contains("claude-opus-5-5") && m.Contains("invalid_request_error"));   // 記錄檔查得到 400 的原因
        Assert.DoesNotContain(logger.Messages, m => m.Contains(FakeApiKey));
    }

    [Fact]
    public async Task ApiKey_IsSentAsHeader_ButNeverLogged()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider, _, logger) = CreateProvider(handler);

        await CollectAsync(provider, SimpleRequest());

        Assert.True(handler.LastRequest!.Headers.TryGetValues("x-api-key", out var values));
        Assert.Contains(FakeApiKey, values!);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(FakeApiKey));
    }

    [Fact]
    public async Task RequestBody_IncludesModelMaxTokensStreamAndThinking()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider, _, _) = CreateProvider(handler);

        await CollectAsync(provider, SimpleRequest("Hi"));

        var body = handler.LastRequestBody!;
        Assert.Contains("\"model\":\"claude-opus-5-5\"", body);
        Assert.Contains("\"max_tokens\":1024", body);
        Assert.Contains("\"stream\":true", body);
        Assert.Contains("adaptive", body);
        Assert.Contains("summarized", body);
    }

    [Fact]
    public async Task ServerFallbacks_WhenEnabled_AddsFallbacksAndBetaHeader()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider, _, _) = CreateProvider(handler, options: new AnthropicProviderOptions { UseServerFallbacks = true });

        await CollectAsync(provider, SimpleRequest());

        var body = handler.LastRequestBody!;
        Assert.Contains("fallbacks", body);
        Assert.Contains("claude-opus-4-8", body);
        Assert.True(handler.LastRequest!.Headers.TryGetValues("anthropic-beta", out var betas));
        Assert.Contains(betas!, b => b.Contains("server-side-fallback"));
    }

    [Theory]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-4-8")]
    public async Task ServerFallbacks_AreNotSent_ForModelsThatDoNotSupportThem(string modelId)
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider, _, _) = CreateProvider(handler, options: new AnthropicProviderOptions { UseServerFallbacks = true });
        var model = new ResolvedModel(new ModelSettings("m1", Connection.Id, modelId, modelId, 1024, 1_000_000, true), Connection);

        await CollectAsync(provider, SimpleRequest() with { Model = model });

        Assert.DoesNotContain("fallbacks", handler.LastRequestBody!);
        Assert.False(handler.LastRequest!.Headers.TryGetValues("anthropic-beta", out var betas) && betas.Any(b => b.Contains("server-side-fallback")));
    }

    [Fact]
    public async Task StreamErrorEvent_MidStream_ProducesFailed()
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("error-event-overloaded"), 64));

        var events = await CollectAsync(provider, SimpleRequest());

        Assert.Equal("剛開始回答", AllText(events));
        Assert.DoesNotContain(events, e => e is Completed);
        var failed = Assert.IsType<Failed>(events[^1]);
        // SDK 對 event: error 一律丟 AnthropicSseException，我們從例外訊息內嵌的原始 JSON
        // 還原出 overloaded_error（見實作紀錄）。
        Assert.Equal(ChatFailureKind.Overloaded, failed.Failure.Kind);
        Assert.True(failed.Failure.Retryable);
    }

    [Fact]
    public async Task Request_UsesConnectionBaseUrl_AndConnectionApiKey()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var secrets = TestSupport.FakeSecretStore.WithKey(FakeApiKey);
        var (provider, _, _) = CreateProvider(handler, secrets);

        await CollectAsync(provider, SimpleRequest());

        Assert.Equal("api.anthropic.test", handler.LastRequest!.RequestUri!.Host);
        Assert.Equal(["connection/test-anthropic/api-key"], secrets.RequestedKeys);
    }

    [Fact]
    public async Task MissingApiKey_MessageNamesTheConnection()
    {
        var (provider, _, _) = CreateProvider(FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64), TestSupport.FakeSecretStore.Empty());

        var events = await CollectAsync(provider, SimpleRequest());

        Assert.Contains("測試連線", Assert.IsType<Failed>(Assert.Single(events)).Failure.Message);
    }

    [Fact]
    public async Task ShowThinkingOff_OmitsThinkingParameter()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider, _, _) = CreateProvider(handler);

        await CollectAsync(provider, SimpleRequest(showThinking: false));

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal(1024, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task OtherProvidersBlocks_AreNotSent_AndEmptiedMessagesAreDropped()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var (provider, _, _) = CreateProvider(handler);
        var request = new ChatRequest(Model(),
        [
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問")], DateTimeOffset.UtcNow),
            new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
                [new ProviderBlock("openai", "something", "{}"), new TextBlock("答")], DateTimeOffset.UtcNow),
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("再問")], DateTimeOffset.UtcNow),
        ]);

        await CollectAsync(provider, request);

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal(1, messages[1].GetProperty("content").GetArrayLength());
    }

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceledException_NotFailed()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 1);
        var (provider, _, _) = CreateProvider(handler);
        using var cts = new CancellationTokenSource();

        async Task ConsumeAndCancel()
        {
            var count = 0;
            await foreach (var _ in provider.StreamAsync(SimpleRequest(), cts.Token))
            {
                count++;
                if (count == 1)
                    await cts.CancelAsync();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(ConsumeAndCancel);
    }
}
