using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Lumi.Providers.Tests;

/// <summary>
/// 長期記憶（第 5 層）與快取：記憶放在 system，所以兩種模式的 system 與 tools 必須逐位元組相同；
/// 只有記憶內容改變時 tools 不變、system 才改變（M14 的 14B，同 M5 的快取前綴測試）。
/// </summary>
public class MemoryPromptCacheTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static readonly ConnectionSettings AnthropicConnection =
        new("a", "Anthropic", ProviderFormat.Anthropic, "https://api.anthropic.test");

    private static readonly ConnectionSettings OpenAiConnection =
        new("o", "OpenAI", ProviderFormat.OpenAICompatible, "https://api.openai.test/v1");

    /// <summary>和 App 的 DI 組出的順序相同：五層，記憶在第 5 層（system）。</summary>
    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new MemoryPromptLayer(), new ModeFormatPromptLayer(),
        new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
    ]);

    private static readonly IReadOnlyList<ChatMessage> History =
    [
        new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("嗨")], Now),
        new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("你好")], Now),
        new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("我的專案進度如何")], Now),
    ];

    private static ResolvedModel ModelFor(ConnectionSettings connection) =>
        new(new ModelSettings("m1", connection.Id, "test-model", "測試", 8000, 200_000, ShowThinking: true), connection);

    private static MemoryItem Item(int id, string content, MemoryCategory category = MemoryCategory.Profile, DateOnly? expires = null) =>
        new(id, content, category, MemorySource.Explicit, null, Now, Now, expires);

    private static MemoryPromptContext Memory(params MemoryItem[] items) => new(items);

    private static MemoryPromptContext SampleMemory() => Memory(
        Item(1, "後端工程師，目前在做內部工具", expires: new DateOnly(2026, 12, 31)),
        Item(3, "偏好以繁體中文回覆", MemoryCategory.Preference),
        Item(2, "專案代號是 Lumi", MemoryCategory.Project));

    private static ChatRequest RequestFor(ResolvedModel model, ChatMode mode, MemoryPromptContext? memory)
    {
        var prompt = Composer.Compose(new PromptContext(mode, PersonaSettings.Default, memory));
        int? cap = mode == ChatMode.Sprite ? 2048 : null;
        return new ChatRequest(model, History, [Lumi.Core.Tools.ToolDefinition.From(new EchoTool())], prompt.System, prompt.TurnSuffix, cap);
    }

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

    private static JsonObject OpenAiBody(ChatRequest request) =>
        JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(request))!.AsObject();

    [Fact]
    public async Task Anthropic_WorkbenchAndSprite_SystemAndTools_AreByteIdentical_WithMemory()
    {
        var model = ModelFor(AnthropicConnection);
        var bench = await SentToAnthropic(RequestFor(model, ChatMode.Workbench, SampleMemory()));
        var sprite = await SentToAnthropic(RequestFor(model, ChatMode.Sprite, SampleMemory()));

        Assert.Contains("<lumi_memory>", (string?)bench["system"]!.AsArray()[0]!["text"]);
        Assert.Equal(bench["system"]!.ToJsonString(), sprite["system"]!.ToJsonString());
        Assert.Equal(bench["tools"]!.ToJsonString(), sprite["tools"]!.ToJsonString());
        Assert.NotEqual(LastText(bench), LastText(sprite));   // 只有模式指示不同
    }

    [Fact]
    public async Task Anthropic_OnlyMemoryChanged_ToolsStayTheSame_SystemChanges()
    {
        var model = ModelFor(AnthropicConnection);
        var before = await SentToAnthropic(RequestFor(model, ChatMode.Workbench, SampleMemory()));
        var after = await SentToAnthropic(RequestFor(model, ChatMode.Workbench,
            Memory(Item(1, "後端工程師，目前在做內部工具", expires: new DateOnly(2026, 12, 31)), Item(4, "新的事實"))));

        Assert.Equal(before["tools"]!.ToJsonString(), after["tools"]!.ToJsonString());
        Assert.NotEqual(before["system"]!.ToJsonString(), after["system"]!.ToJsonString());
        Assert.Contains("新的事實", (string?)after["system"]!.AsArray()[0]!["text"]);
    }

    [Fact]
    public async Task Anthropic_NoChange_TwoConversions_SystemIsByteIdentical()
    {
        var model = ModelFor(AnthropicConnection);
        var first = await SentToAnthropic(RequestFor(model, ChatMode.Workbench, SampleMemory()));
        var second = await SentToAnthropic(RequestFor(model, ChatMode.Workbench, SampleMemory()));

        Assert.Equal(first["system"]!.ToJsonString(), second["system"]!.ToJsonString());
        Assert.Equal(first["tools"]!.ToJsonString(), second["tools"]!.ToJsonString());
    }

    [Fact]
    public async Task Anthropic_MemoryOff_SystemHasNoMemoryText()
    {
        var body = await SentToAnthropic(RequestFor(ModelFor(AnthropicConnection), ChatMode.Workbench, memory: null));

        Assert.DoesNotContain("lumi_memory", body["system"]!.ToJsonString());
        Assert.DoesNotContain("長期記憶", body["system"]!.ToJsonString());
    }

    [Fact]
    public async Task Anthropic_WorstCase_100Items_SystemLengthIsReported()
    {
        // 100 則、每則約 20 字（總計約 2,000 字）：設定上限的最壞情況
        var items = Enumerable.Range(1, 100)
            .Select(i => Item(i, $"記憶{i:000}".PadRight(20, '字'), (MemoryCategory)(i % 4)))
            .ToArray();
        var model = ModelFor(AnthropicConnection);

        var body = await SentToAnthropic(RequestFor(model, ChatMode.Workbench, Memory(items)));
        var system = (string)body["system"]!.AsArray()[0]!["text"]!;
        var memoryBlock = system[system.IndexOf("<lumi_memory>", StringComparison.Ordinal)..];
        var totalContent = items.Sum(i => MemoryRules.Length(i.Content));

        output.WriteLine($"worst case: 100 items, {totalContent} content chars; system = {system.Length} chars (UTF-16), memory section = {memoryBlock.Length} chars");
        Assert.Equal(100, system.Split('\n').Count(l => l.StartsWith("[m", StringComparison.Ordinal)));
        Assert.Equal(2000, totalContent);
        Assert.True(system.Length > totalContent);
    }

    [Fact]
    public void OpenAI_SystemMessage_CarriesMemory_AndIsIdenticalAcrossModes()
    {
        var model = ModelFor(OpenAiConnection);
        var bench = OpenAiBody(RequestFor(model, ChatMode.Workbench, SampleMemory()));
        var sprite = OpenAiBody(RequestFor(model, ChatMode.Sprite, SampleMemory()));

        var benchSystem = (string?)bench["messages"]!.AsArray()[0]!["content"];
        Assert.Equal("system", (string?)bench["messages"]!.AsArray()[0]!["role"]);
        Assert.Contains("<lumi_memory>", benchSystem);
        Assert.Contains("[m1] [本人] 後端工程師，目前在做內部工具 (有效至 2026-12-31)", benchSystem);
        Assert.Equal(benchSystem, (string?)sprite["messages"]!.AsArray()[0]!["content"]);
        Assert.Equal(bench["tools"]!.ToJsonString(), sprite["tools"]!.ToJsonString());
    }

    private static string? LastText(JsonObject body)
    {
        var content = body["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        return (string?)content[^1]!["text"];
    }
}
