using System.Net;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;

namespace Lumi.Providers.Tests;

public class ConnectionTesterTests
{
    private const string FormKey = "sk-from-the-form-0000";
    private const string StoredKey = "sk-stored-in-keychain-1111";

    private static readonly ConnectionSettings OpenAi = new("c-openai", "OpenAI", ProviderFormat.OpenAICompatible, "https://api.openai.test/v1");
    private static readonly ConnectionSettings Claude = new("c-claude", "Anthropic", ProviderFormat.Anthropic, "https://api.anthropic.test");

    private static OpenAICompatibleConnectionTester OpenAiTester(FakeAnthropicHttpHandler handler, TestSupport.FakeSecretStore? secrets = null) =>
        new(new HttpClient(handler), secrets ?? TestSupport.FakeSecretStore.WithKey(StoredKey));

    private static AnthropicConnectionTester ClaudeTester(FakeAnthropicHttpHandler handler, TestSupport.FakeSecretStore? secrets = null) =>
        new(new HttpClient(handler), secrets ?? TestSupport.FakeSecretStore.WithKey(StoredKey));

    // ---- OpenAI 相容 ----

    [Fact]
    public async Task OpenAI_Success_ListsDistinctSortedModels()
    {
        var handler = FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models.json"));

        var result = await OpenAiTester(handler).TestAsync(OpenAi, null, default);

        Assert.True(result.Success);
        Assert.Equal(["gpt-test", "llama3"], result.ModelIds);
        Assert.Equal("https://api.openai.test/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task OpenAI_UsesFormKeyWhenGiven_OtherwiseStoredKey_AndNeverWritesIt()
    {
        var handler = FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models.json"));
        var secrets = TestSupport.FakeSecretStore.WithKey(StoredKey);

        await OpenAiTester(handler, secrets).TestAsync(OpenAi, new SecretValue(FormKey), default);
        Assert.Equal(FormKey, handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Empty(secrets.RequestedKeys);   // 用表單的金鑰時不去碰鑰匙圈

        await OpenAiTester(handler, secrets).TestAsync(OpenAi, null, default);
        Assert.Equal(StoredKey, handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal(["connection/c-openai/api-key"], secrets.RequestedKeys);
    }

    [Fact]
    public async Task OpenAI_NoKeyAnywhere_SendsNoAuthorization()
    {
        var handler = FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models.json"));

        var result = await OpenAiTester(handler, TestSupport.FakeSecretStore.Empty()).TestAsync(OpenAi, null, default);

        Assert.True(result.Success);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task OpenAI_EmptyList_IsSuccessWithNoModels()
    {
        var result = await OpenAiTester(FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models-empty.json"))).TestAsync(OpenAi, null, default);

        Assert.True(result.Success);
        Assert.Empty(result.ModelIds);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401", ChatFailureKind.InvalidApiKey)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503", ChatFailureKind.ServerError)]
    public async Task OpenAI_HttpErrors_AreClassified(HttpStatusCode status, string fixture, ChatFailureKind kind)
    {
        var handler = FakeAnthropicHttpHandler.ForError(status, Fixtures.OpenAIHttpError(fixture));

        var result = await OpenAiTester(handler).TestAsync(OpenAi, null, default);

        Assert.False(result.Success);
        Assert.Equal(kind, result.Failure!.Kind);
    }

    [Fact]
    public async Task OpenAI_NotFound_SuggestsCheckingBaseUrlOrTypingModelManually()
    {
        var handler = FakeAnthropicHttpHandler.ForError(HttpStatusCode.NotFound, Fixtures.OpenAIHttpError("404"));

        var result = await OpenAiTester(handler).TestAsync(OpenAi, null, default);

        Assert.Equal(ChatFailureKind.ModelNotFound, result.Failure!.Kind);
        Assert.Contains("Base URL", result.Failure.Message);
        Assert.Contains("手動輸入", result.Failure.Message);
    }

    [Fact]
    public async Task OpenAI_ServiceDown_IsNetworkFailure()
    {
        var handler = FakeAnthropicHttpHandler.ForException(new HttpRequestException("Connection refused"));

        var result = await OpenAiTester(handler).TestAsync(OpenAi, null, default);

        Assert.Equal(ChatFailureKind.Network, result.Failure!.Kind);
        Assert.Contains("api.openai.test", result.Failure.Message);
    }

    [Fact]
    public async Task OpenAI_GarbageBody_IsReportedNotThrown()
    {
        var result = await OpenAiTester(FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models-garbage.txt"))).TestAsync(OpenAi, null, default);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task OpenAI_UnreadableStoredKey_IsReported()
    {
        var secrets = TestSupport.FakeSecretStore.Throwing(new SecretStoreException("x"));

        var result = await OpenAiTester(FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models.json")), secrets).TestAsync(OpenAi, null, default);

        Assert.Equal(ChatFailureKind.SecretUnreadable, result.Failure!.Kind);
    }

    [Fact]
    public async Task OpenAI_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiTester(FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models.json"))).TestAsync(OpenAi, null, cts.Token));
    }

    // ---- Anthropic ----

    [Fact]
    public async Task Anthropic_Success_ListsModels()
    {
        var handler = FakeAnthropicHttpHandler.ForJson(Fixtures.AnthropicFile("models.json"));

        var result = await ClaudeTester(handler).TestAsync(Claude, new SecretValue(FormKey), default);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5"], result.ModelIds);
        Assert.Equal("api.anthropic.test", handler.LastRequest!.RequestUri!.Host);
        Assert.True(handler.LastRequest.Headers.TryGetValues("x-api-key", out var keys));
        Assert.Equal([FormKey], keys);
    }

    [Fact]
    public async Task Anthropic_NoKey_AsksForOne_WithoutHttpCall()
    {
        var handler = FakeAnthropicHttpHandler.ForJson(Fixtures.AnthropicFile("models.json"));

        var result = await ClaudeTester(handler, TestSupport.FakeSecretStore.Empty()).TestAsync(Claude, null, default);

        Assert.Equal(ChatFailureKind.MissingApiKey, result.Failure!.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Anthropic_BadKey_IsClassified()
    {
        var handler = FakeAnthropicHttpHandler.ForError(HttpStatusCode.Unauthorized, Fixtures.HttpError("401"));

        var result = await ClaudeTester(handler).TestAsync(Claude, new SecretValue(FormKey), default);

        Assert.Equal(ChatFailureKind.InvalidApiKey, result.Failure!.Kind);
    }

    // ---- 分派 ----

    [Fact]
    public async Task Routing_PicksTesterByFormat()
    {
        var openAiHandler = FakeAnthropicHttpHandler.ForJson(Fixtures.OpenAIFile("models.json"));
        var claudeHandler = FakeAnthropicHttpHandler.ForJson(Fixtures.AnthropicFile("models.json"));
        var router = new RoutingConnectionTester(ClaudeTester(claudeHandler), OpenAiTester(openAiHandler));

        await router.TestAsync(OpenAi, null);
        await router.TestAsync(Claude, null);

        Assert.Equal(1, openAiHandler.CallCount);
        Assert.Equal(1, claudeHandler.CallCount);
    }
}
