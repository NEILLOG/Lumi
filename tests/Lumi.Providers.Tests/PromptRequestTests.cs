using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

/// <summary>
/// system、快取斷點與模式指示怎麼送出去（M5 的 5B）。核心保證：同一段歷史在工作台與小精靈組出的請求，
/// 除了模式指示和 <c>max_tokens</c>，其餘逐位元組相同——前綴一致，快取才會命中。
/// </summary>
public class PromptRequestTests
{
    private static readonly ConnectionSettings AnthropicConnection =
        new("a", "Anthropic", ProviderFormat.Anthropic, "https://api.anthropic.test");

    private static readonly ConnectionSettings OpenAiConnection =
        new("o", "OpenAI", ProviderFormat.OpenAICompatible, "https://api.openai.test/v1");

    private static ResolvedModel ModelFor(ConnectionSettings connection) =>
        new(new ModelSettings("m1", connection.Id, "test-model", "測試", 8000, 200_000, ShowThinking: true), connection);

    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new ModeFormatPromptLayer(), new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
    ]);

    private static ChatMessage Msg(ChatRole role, params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UtcNow);

    private static ToolResultBlock Result(string id) => new(id, "結果", false, "回聲", null, null, 1, ToolApproval.NotRequired);

    private static readonly IReadOnlyList<ChatMessage> EndsWithUser =
    [
        Msg(ChatRole.User, new TextBlock("嗨")),
        Msg(ChatRole.Assistant, new TextBlock("你好")),
        Msg(ChatRole.User, new ContextBlock(ContextKind.File, "a.txt", "/x/a.txt", "內容", Embedded: true), new TextBlock("看一下")),
    ];

    private static readonly IReadOnlyList<ChatMessage> EndsWithToolResult =
    [
        Msg(ChatRole.User, new TextBlock("回聲")),
        Msg(ChatRole.Assistant, new TextBlock("好"), new ToolUseBlock("t1", "echo", """{"text":"嗨"}""")),
        Msg(ChatRole.Tool, Result("t1")),
    ];

    public static TheoryData<string> Histories => new() { nameof(EndsWithUser), nameof(EndsWithToolResult) };

    private static IReadOnlyList<ChatMessage> History(string name) => name == nameof(EndsWithUser) ? EndsWithUser : EndsWithToolResult;

    private static ChatRequest RequestFor(ResolvedModel model, IReadOnlyList<ChatMessage> history, ChatMode mode, bool withTools = true)
    {
        var prompt = Composer.Compose(new PromptContext(mode, PersonaSettings.Default));
        int? cap = mode == ChatMode.Sprite ? 2048 : null;
        return new ChatRequest(model, history, withTools ? [Lumi.Core.Tools.ToolDefinition.From(new EchoTool())] : null, prompt.System, prompt.TurnSuffix, cap);
    }

    // ================= Anthropic =================

    private static async Task<JsonObject> SentToAnthropic(ChatRequest request)
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var provider = new AnthropicChatProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") },
            FakeSecretStore.WithKey("sk-ant-test-FAKE-KEY-0000"),
            Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }),
            new CapturingLogger<AnthropicChatProvider>());
        await foreach (var _ in provider.StreamAsync(request)) { }
        return JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
    }

    private static JsonArray LastMessageContent(JsonObject body) =>
        body["messages"]!.AsArray()[^1]!["content"]!.AsArray();

    private static int CountCacheControl(JsonNode node) => node switch
    {
        JsonObject o => o.Sum(p => (p.Key == "cache_control" ? 1 : 0) + (p.Value is null ? 0 : CountCacheControl(p.Value))),
        JsonArray a => a.Sum(x => x is null ? 0 : CountCacheControl(x)),
        _ => 0,
    };

    [Fact]
    public async Task Anthropic_SystemIsSentAsCachedTextBlock()
    {
        var request = RequestFor(ModelFor(AnthropicConnection), EndsWithUser, ChatMode.Workbench);

        var body = await SentToAnthropic(request);

        var system = Assert.Single(body["system"]!.AsArray())!.AsObject();
        Assert.Equal("text", (string?)system["type"]);
        Assert.Equal(request.System, (string?)system["text"]);
        Assert.Equal("ephemeral", (string?)system["cache_control"]!["type"]);
    }

    [Fact]
    public async Task Anthropic_AMessageThatWasOnceLast_KeepsItsSuffix_SoThePrefixIsByteForByteTheSameNextTime()
    {
        // thinking 簽章綁定它之前的全部內容：第一次它是最後一則（指示在斷點之後），第二次是歷史的一部分，兩次的內容必須一樣
        var model = ModelFor(AnthropicConnection);
        var suffix1 = "<lumi_mode>\n工作台\n</lumi_mode>";
        var suffix2 = "<lumi_mode>\n小精靈\n</lumi_mode>";
        var user1 = Msg(ChatRole.User, new TextBlock("嗨")) with { TurnSuffix = suffix1 };
        var assistant = Msg(ChatRole.Assistant, new TextBlock("你好"));
        var user2 = Msg(ChatRole.User, new TextBlock("再問")) with { TurnSuffix = suffix2 };

        var first = await SentToAnthropic(new ChatRequest(model, [user1], null, "系統", suffix1));
        var second = await SentToAnthropic(new ChatRequest(model, [user1, assistant, user2], null, "系統", suffix2));

        static JsonNode WithoutCacheControl(JsonNode node)
        {
            var copy = JsonNode.Parse(node.ToJsonString())!;
            foreach (var block in copy.AsArray())
                block!.AsObject().Remove("cache_control");
            return copy;
        }
        var before = WithoutCacheControl(first["messages"]!.AsArray()[0]!["content"]!);
        var after = WithoutCacheControl(second["messages"]!.AsArray()[0]!["content"]!);
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
        var afterBlocks = after.AsArray();
        Assert.Equal(suffix1, (string?)afterBlocks[afterBlocks.Count - 1]!["text"]);   // 不是被拿掉，而是留在原位
        // 最後一則照舊：斷點在指示之前，指示是自己的 text 區塊
        Assert.Equal(suffix2, (string?)LastMessageContent(second)[^1]!["text"]);
        Assert.Equal(2, CountCacheControl(second));   // system + 歷史最後
    }

    [Fact]
    public async Task Anthropic_NoSystem_NoSuffix_SendsNeitherField_AndOnlyTheHistoryBreakpoint()
    {
        var body = await SentToAnthropic(new ChatRequest(ModelFor(AnthropicConnection), EndsWithUser));

        Assert.False(body.ContainsKey("system"));
        Assert.Equal(1, CountCacheControl(body));   // 歷史最後一個區塊
        Assert.DoesNotContain("lumi_mode", body.ToJsonString());   // 沒有指示時也不會有轉義後的形式
    }

    [Theory]
    [MemberData(nameof(Histories))]
    public async Task Anthropic_BreakpointIsOnLastHistoryBlock_SuffixComesAfterWithoutCache(string history)
    {
        var request = RequestFor(ModelFor(AnthropicConnection), History(history), ChatMode.Sprite);

        var body = await SentToAnthropic(request);

        var content = LastMessageContent(body);
        var suffix = content[^1]!.AsObject();
        Assert.Equal(request.TurnSuffix, (string?)suffix["text"]);
        Assert.Null(suffix["cache_control"]);
        var beforeSuffix = content[^2]!.AsObject();
        Assert.Equal("ephemeral", (string?)beforeSuffix["cache_control"]!["type"]);
        Assert.Equal(2, CountCacheControl(body));   // system + 歷史最後；上限是 4
        // 模式指示只有一份，在最後一則訊息裡（JSON 會把 < 轉義，所以看解析後的文字）
        var allTexts = body["messages"]!.AsArray().SelectMany(m => m!["content"]!.AsArray()).Select(b => (string?)b!["text"]);
        Assert.Single(allTexts, t => t is not null && t.Contains("<lumi_mode>"));
    }

    [Fact]
    public async Task Anthropic_ToolResultStaysFirst_WhenSuffixIsAppended()
    {
        var body = await SentToAnthropic(RequestFor(ModelFor(AnthropicConnection), EndsWithToolResult, ChatMode.Workbench));

        var content = LastMessageContent(body);
        Assert.Equal("tool_result", (string?)content[0]!["type"]);
        Assert.Equal("text", (string?)content[^1]!["type"]);
        Assert.NotNull(content[0]!["cache_control"]);   // 斷點在 tool_result 上（它是歷史的最後一個區塊）
    }

    [Theory]
    [MemberData(nameof(Histories))]
    public async Task Anthropic_PrefixIsByteIdentical_AcrossModes(string history)
    {
        var model = ModelFor(AnthropicConnection);
        var bench = await SentToAnthropic(RequestFor(model, History(history), ChatMode.Workbench));
        var sprite = await SentToAnthropic(RequestFor(model, History(history), ChatMode.Sprite));

        // 差異只能是：模式指示（最後一則訊息的最後一個區塊）與 max_tokens
        Assert.NotEqual((string?)LastMessageContent(bench)[^1]!["text"], (string?)LastMessageContent(sprite)[^1]!["text"]);
        Assert.Equal(8000, (int)bench["max_tokens"]!);
        Assert.Equal(2048, (int)sprite["max_tokens"]!);

        foreach (var body in new[] { bench, sprite })
        {
            var content = LastMessageContent(body);
            content.RemoveAt(content.Count - 1);
            body.Remove("max_tokens");
        }
        Assert.Equal(bench.ToJsonString(), sprite.ToJsonString());
        Assert.NotNull(bench["tools"]);
        Assert.NotNull(bench["thinking"]);   // thinking 不因模式改變
    }

    [Fact]
    public async Task Anthropic_MaxTokens_UsesRequestOverrideOrModelSetting()
    {
        var model = ModelFor(AnthropicConnection);

        Assert.Equal(8000, (int)(await SentToAnthropic(new ChatRequest(model, EndsWithUser)))["max_tokens"]!);
        Assert.Equal(500, (int)(await SentToAnthropic(new ChatRequest(model, EndsWithUser, MaxOutputTokens: 500)))["max_tokens"]!);
    }

    // ================= OpenAI 相容 =================

    private static JsonArray OpenAiMessages(ChatRequest request) =>
        JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(request))!["messages"]!.AsArray();

    [Fact]
    public void OpenAI_SystemIsTheFirstMessage()
    {
        var request = RequestFor(ModelFor(OpenAiConnection), EndsWithUser, ChatMode.Workbench);

        var first = OpenAiMessages(request)[0]!.AsObject();

        Assert.Equal("system", (string?)first["role"]);
        Assert.Equal(request.System, (string?)first["content"]);
    }

    [Fact]
    public void OpenAI_NoSystem_NoSuffix_BodyIsUnchanged()
    {
        var messages = OpenAiMessages(new ChatRequest(ModelFor(OpenAiConnection), EndsWithUser));

        Assert.Equal(["user", "assistant", "user"], messages.Select(m => (string?)m!["role"]));
        Assert.DoesNotContain("lumi_mode", messages.ToJsonString());
    }

    [Fact]
    public void OpenAI_SuffixJoinsLastUserMessage()
    {
        var request = RequestFor(ModelFor(OpenAiConnection), EndsWithUser, ChatMode.Sprite);

        var messages = OpenAiMessages(request);

        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => (string?)m!["role"]));
        var text = (string)messages[^1]!["content"]!;
        Assert.EndsWith("\n\n" + request.TurnSuffix, text);
        Assert.Contains("看一下", text);
    }

    [Fact]
    public void OpenAI_SuffixJoinsTextPartOfLastUserMessage_WhenItHasImages()
    {
        var withImage = new List<ChatMessage>
        {
            Msg(ChatRole.User, new ImageBlock("pic.png", "image/png", "pic.png"), new TextBlock("這是什麼")),
        };
        var request = RequestFor(ModelFor(OpenAiConnection), withImage, ChatMode.Workbench);
        var images = new Dictionary<string, byte[]> { ["pic.png"] = [1, 2, 3] };

        var parts = JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(request, images))!["messages"]!.AsArray()[^1]!["content"]!.AsArray();

        Assert.Equal(["text", "image_url"], parts.Select(p => (string?)p!["type"]));
        Assert.EndsWith(request.TurnSuffix!, (string)parts[0]!["text"]!);
    }

    [Fact]
    public void OpenAI_SuffixBecomesSeparateUserMessage_WhenLastIsToolResult()
    {
        var request = RequestFor(ModelFor(OpenAiConnection), EndsWithToolResult, ChatMode.Sprite);

        var messages = OpenAiMessages(request);

        Assert.Equal(["system", "user", "assistant", "tool", "user"], messages.Select(m => (string?)m!["role"]));
        Assert.Equal(request.TurnSuffix, (string?)messages[^1]!["content"]);
    }

    [Theory]
    [MemberData(nameof(Histories))]
    public void OpenAI_PrefixIsByteIdentical_AcrossModes(string history)
    {
        var model = ModelFor(OpenAiConnection);
        var bench = JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(RequestFor(model, History(history), ChatMode.Workbench)))!.AsObject();
        var sprite = JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(RequestFor(model, History(history), ChatMode.Sprite)))!.AsObject();

        Assert.Equal(8000, (int)bench["max_tokens"]!);
        Assert.Equal(2048, (int)sprite["max_tokens"]!);

        // 去掉模式指示後（最後一則 user 訊息的結尾，或單獨的最後一則 user 訊息）應該完全相同
        foreach (var body in new[] { bench, sprite })
        {
            body.Remove("max_tokens");
            var messages = body["messages"]!.AsArray();
            var last = messages[^1]!.AsObject();
            var content = (string)last["content"]!;
            var at = content.IndexOf("<lumi_mode>", StringComparison.Ordinal);
            if (at == 0)
                messages.RemoveAt(messages.Count - 1);
            else
                last["content"] = content[..at];
        }
        Assert.Equal(bench.ToJsonString(), sprite.ToJsonString());
        Assert.Contains("\"tools\"", bench.ToJsonString());
    }

    [Fact]
    public void OpenAI_MaxTokens_UsesRequestOverrideOrModelSetting()
    {
        var model = ModelFor(OpenAiConnection);

        Assert.Equal(8000, (int)JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(new ChatRequest(model, EndsWithUser)))!["max_tokens"]!);
        Assert.Equal(500, (int)JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(new ChatRequest(model, EndsWithUser, MaxOutputTokens: 500)))!["max_tokens"]!);
    }
}
