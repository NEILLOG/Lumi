using System.Net;
using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

/// <summary>M22 22H：「請求超過模型上下文長度」的錯誤兩家供應商都對應成 <see cref="ChatFailureKind.ContextTooLong"/>。</summary>
public class ContextTooLongTests
{
    private static readonly ConnectionSettings AnthropicConnection = new("a", "A", ProviderFormat.Anthropic, "https://api.anthropic.test");
    private static readonly ConnectionSettings OpenAiConnection = new("o", "O", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static ChatRequest Request(ConnectionSettings connection) => new(
        new ResolvedModel(new ModelSettings("m", connection.Id, "model", "M", 1024, 100_000, true), connection),
        [new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("Hi")], DateTimeOffset.UtcNow)]);

    private static async Task<Failed> RunAnthropic(HttpStatusCode status, string body)
    {
        var provider = new AnthropicChatProvider(
            new HttpClient(FakeAnthropicHttpHandler.ForError(status, Encoding.UTF8.GetBytes(body))) { BaseAddress = new Uri("https://api.anthropic.test/") },
            FakeSecretStore.WithKey("sk-ant-test-FAKE"), Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }),
            new CapturingLogger<AnthropicChatProvider>());
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(Request(AnthropicConnection)))
            events.Add(e);
        return Assert.IsType<Failed>(Assert.Single(events));
    }

    private static async Task<Failed> RunOpenAi(HttpStatusCode status, string body)
    {
        var provider = new OpenAICompatibleChatProvider(
            new HttpClient(FakeAnthropicHttpHandler.ForError(status, Encoding.UTF8.GetBytes(body))),
            FakeSecretStore.WithKey("sk-test"), new CapturingLogger<OpenAICompatibleChatProvider>());
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(Request(OpenAiConnection)))
            events.Add(e);
        return Assert.IsType<Failed>(Assert.Single(events));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "prompt is too long: 250000 tokens > 200000 maximum")]
    [InlineData(HttpStatusCode.BadRequest, "Input is too long. Too many tokens in the request.")]
    public async Task Anthropic_TooLongPrompt_MapsToContextTooLong(HttpStatusCode status, string message)
    {
        var failed = await RunAnthropic(status, $$"""{"type":"error","error":{"type":"invalid_request_error","message":"{{message}}"},"request_id":"req_1"}""");

        Assert.Equal(ChatFailureKind.ContextTooLong, failed.Failure.Kind);
        Assert.False(failed.Failure.Retryable);
        Assert.DoesNotContain("250000", failed.Failure.Message);   // 伺服器的內文不進訊息
    }

    [Fact]
    public async Task Anthropic_OtherBadRequests_StayBadRequest()
    {
        var failed = await RunAnthropic(HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"messages: roles must alternate"},"request_id":"req_1"}""");

        Assert.Equal(ChatFailureKind.BadRequest, failed.Failure.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"context_length_exceeded","message":"x"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"This model's maximum context length is 8192 tokens. However, you requested 9000 tokens."}}""")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, """{"error":{"message":"Too many tokens in request"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"PROMPT IS TOO LONG"}}""")]
    public async Task OpenAi_TooLongPrompt_MapsToContextTooLong(HttpStatusCode status, string body)
    {
        var failed = await RunOpenAi(status, body);

        Assert.Equal(ChatFailureKind.ContextTooLong, failed.Failure.Kind);
        Assert.False(failed.Failure.Retryable);
        Assert.DoesNotContain("8192", failed.Failure.Message);
    }

    [Fact]
    public async Task OpenAi_OtherBadRequests_StayBadRequest()
    {
        var failed = await RunOpenAi(HttpStatusCode.BadRequest, """{"error":{"message":"invalid request"}}""");

        Assert.Equal(ChatFailureKind.BadRequest, failed.Failure.Kind);
    }
}
