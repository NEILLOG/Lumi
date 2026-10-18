using System.Net;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;

namespace Lumi.Providers.Tests;

public class OpenAICompatibleChatProviderTests
{
    private const string FakeApiKey = "sk-openai-FAKE-KEY-1234";

    private static readonly ConnectionSettings Connection =
        new("test-openai", "測試 OpenAI", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static ResolvedModel Model() =>
        new(new ModelSettings("m1", Connection.Id, "gpt-test", "GPT Test", 2048, 128_000, true), Connection);

    private static ChatRequest SimpleRequest(string text = "Hi") => new(Model(),
        [new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow)]);

    private static (OpenAICompatibleChatProvider Provider, CapturingLogger<OpenAICompatibleChatProvider> Logger) Create(
        FakeAnthropicHttpHandler handler, TestSupport.FakeSecretStore? secrets = null)
    {
        var logger = new CapturingLogger<OpenAICompatibleChatProvider>();
        return (new OpenAICompatibleChatProvider(new HttpClient(handler), secrets ?? TestSupport.FakeSecretStore.WithKey(FakeApiKey), logger), logger);
    }

    private static async Task<List<ChatEvent>> Collect(OpenAICompatibleChatProvider provider, ChatRequest request, CancellationToken ct = default)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(request, ct))
            events.Add(e);
        return events;
    }

    private static string AllText(IEnumerable<ChatEvent> events) => string.Concat(events.OfType<TextDelta>().Select(e => e.Text));

    private static void AssertEndsWithExactlyOneTerminalEvent(List<ChatEvent> events)
    {
        var terminal = events.Select((e, i) => (e, i)).Where(x => x.e is Completed or Failed).Select(x => x.i).ToList();
        Assert.Single(terminal);
        Assert.Equal(events.Count - 1, terminal[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task BasicText_StreamsAndCompletes_WithUsage(int chunkSize)
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), chunkSize));

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal("Hello, world!", AllText(events));
        AssertEndsWithExactlyOneTerminalEvent(events);
        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal(StopReason.EndTurn, completed.Reason);
        Assert.Equal("Hello, world!", Assert.IsType<TextBlock>(Assert.Single(completed.Message.Content)).Text);
        Assert.Equal("gpt-test", completed.Message.ModelId);
        // prompt_tokens 25 含 10 個快取：InputTokens 不含快取，快取另外記
        Assert.Equal(new TokenUsage(15, 6, 0, 10), completed.Message.Usage);
        Assert.Contains(events, e => e is UsageEvent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task UnicodeAndCombinedEmoji_SurviveChunking(int chunkSize)
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("unicode-emoji"), chunkSize));

        var events = await Collect(provider, SimpleRequest());

        const string expected = "中文與組合 emoji：❤️‍🔥 和國旗 🇹🇼 完整無缺。";
        Assert.Equal(expected, AllText(events));
        Assert.Equal(expected, Assert.IsType<TextBlock>(Assert.Single(Assert.IsType<Completed>(events[^1]).Message.Content)).Text);
    }

    [Fact]
    public async Task ReasoningContent_IsShownButNotStored()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("reasoning-content"), 16));

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal("先想一下。再想一下。", string.Concat(events.OfType<ThinkingDelta>().Select(e => e.Text)));
        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.DoesNotContain(completed.Message.Content, b => b is ThinkingBlock);
        Assert.Equal("答案是 42。", Assert.IsType<TextBlock>(Assert.Single(completed.Message.Content)).Text);
    }

    [Fact]
    public async Task NoUsage_AndCommentsAndUnknownFields_AreTolerated()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("no-usage"), 16));

        var events = await Collect(provider, SimpleRequest());

        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Null(completed.Message.Usage);
        Assert.DoesNotContain(events, e => e is UsageEvent);
        Assert.Equal("沒有 usage 的伺服器。", AllText(events));
    }

    [Fact]
    public async Task FinishLength_MapsToMaxTokens()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("finish-length"), 16));

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal(StopReason.MaxTokens, Assert.IsType<Completed>(events[^1]).Reason);
    }

    [Fact]
    public async Task ContentFilter_ProducesFailedRefused_NoCompleted()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("content-filter"), 16));

        var events = await Collect(provider, SimpleRequest());

        Assert.DoesNotContain(events, e => e is Completed);
        Assert.Equal(ChatFailureKind.Refused, Assert.IsType<Failed>(events[^1]).Failure.Kind);
        AssertEndsWithExactlyOneTerminalEvent(events);
    }

    [Fact]
    public async Task ErrorInsideStream_ProducesFailed()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("error-in-stream"), 16));

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal("剛開始回答", AllText(events));
        Assert.Equal(ChatFailureKind.StreamError, Assert.IsType<Failed>(events[^1]).Failure.Kind);
        AssertEndsWithExactlyOneTerminalEvent(events);
    }

    [Fact]
    public async Task StreamEndingWithoutDoneOrFinishReason_IsAFailure()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("truncated"), 16));

        var events = await Collect(provider, SimpleRequest());

        Assert.DoesNotContain(events, e => e is Completed);
        Assert.Equal(ChatFailureKind.StreamError, Assert.IsType<Failed>(events[^1]).Failure.Kind);
    }

    [Fact]
    public async Task DoneWithoutFinishReason_StillCompletes()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("done-without-finish"), 16));

        var events = await Collect(provider, SimpleRequest());

        Assert.IsType<Completed>(events[^1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "400", ChatFailureKind.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, "401", ChatFailureKind.InvalidApiKey, false)]
    [InlineData(HttpStatusCode.NotFound, "404", ChatFailureKind.ModelNotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, "429", ChatFailureKind.RateLimited, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503", ChatFailureKind.ServerError, true)]
    public async Task HttpErrors_AreMapped(HttpStatusCode status, string fixture, ChatFailureKind kind, bool retryable)
    {
        var (provider, logger) = Create(FakeAnthropicHttpHandler.ForError(status, Fixtures.OpenAIHttpError(fixture)));

        var events = await Collect(provider, SimpleRequest());

        var failed = Assert.IsType<Failed>(Assert.Single(events));
        Assert.Equal(kind, failed.Failure.Kind);
        Assert.Equal(retryable, failed.Failure.Retryable);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(FakeApiKey));
        // 伺服器回的錯誤內文不進 ChatFailure
        Assert.DoesNotContain("Incorrect API key provided", failed.Failure.Message);
    }

    [Fact]
    public async Task RateLimit_CarriesRetryAfter()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForError(HttpStatusCode.TooManyRequests, Fixtures.OpenAIHttpError("429"), TimeSpan.FromSeconds(7)));

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.IsType<Failed>(Assert.Single(events)).Failure.RetryAfter);
    }

    [Fact]
    public async Task ServerNotReachable_IsNetworkFailure_MentioningHost()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForException(new HttpRequestException("Connection refused")));

        var events = await Collect(provider, SimpleRequest());

        var failure = Assert.IsType<Failed>(Assert.Single(events)).Failure;
        Assert.Equal(ChatFailureKind.Network, failure.Kind);
        Assert.Contains("localhost", failure.Message);
    }

    [Fact]
    public async Task WithKey_SendsBearer_AndNeverLogsIt()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var (provider, logger) = Create(handler);

        await Collect(provider, SimpleRequest());

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal(FakeApiKey, handler.LastRequest.Headers.Authorization.Parameter);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(FakeApiKey));
    }

    [Fact]
    public async Task WithoutKey_SendsNoAuthorizationHeader_AndStillWorks()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var (provider, _) = Create(handler, TestSupport.FakeSecretStore.Empty());

        var events = await Collect(provider, SimpleRequest());

        Assert.Null(handler.LastRequest!.Headers.Authorization);
        Assert.IsType<Completed>(events[^1]);
    }

    [Fact]
    public async Task UnreadableKey_FailsWithoutHttpCall()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var (provider, _) = Create(handler, TestSupport.FakeSecretStore.Throwing(new Lumi.Core.Secrets.SecretStoreException("x")));

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal(ChatFailureKind.SecretUnreadable, Assert.IsType<Failed>(Assert.Single(events)).Failure.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Request_UsesConnectionKeyName_AndCorrectUrlAndBody()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);
        var secrets = TestSupport.FakeSecretStore.WithKey(FakeApiKey);
        var (provider, _) = Create(handler, secrets);

        await Collect(provider, SimpleRequest("你好"));

        Assert.Equal(["connection/test-openai/api-key"], secrets.RequestedKeys);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("你好", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Body_SendsOnlyText_AndDropsEmptiedMessages()
    {
        var request = new ChatRequest(Model(),
        [
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問")], DateTimeOffset.UtcNow),
            new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
                [new ThinkingBlock("想", "sig"), new ProviderBlock("anthropic", "fallback", "{}"), new TextBlock("答")], DateTimeOffset.UtcNow),
            new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new ThinkingBlock("只有思考", "sig")], DateTimeOffset.UtcNow),
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("再問")], DateTimeOffset.UtcNow),
        ]);

        using var doc = JsonDocument.Parse(OpenAICompatibleChatProvider.BuildBody(request));
        var messages = doc.RootElement.GetProperty("messages");

        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("答", messages[1].GetProperty("content").GetString());
        Assert.DoesNotContain("sig", doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceledException_NotFailed()
    {
        var (provider, _) = Create(FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 1));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var count = 0;
            await foreach (var _ in provider.StreamAsync(SimpleRequest(), cts.Token))
            {
                if (++count == 1)
                    await cts.CancelAsync();
            }
        });
    }
}
