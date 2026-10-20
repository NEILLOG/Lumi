using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>對話迴圈和 prompt 分層的接合（M5 的 5A）：system／模式指示、依模式選模型、小精靈的輸出上限。</summary>
public class ConversationPromptTests
{
    private static readonly ModelSettings SpriteModel =
        new("sprite-model-settings", FakeModelCatalog.Connection.Id, "small-model", "小模型", 8000, 200_000, true);

    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new ModeFormatPromptLayer(), new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
    ]);

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ConversationService Service(IChatProvider provider, InMemoryConversationStore store, FakeModelCatalog catalog,
        PromptOptions? options = null, IEnumerable<ITool>? tools = null) =>
        new(provider, store, catalog, new ToolRegistry(tools ?? []), new ScriptedApprovalService(), new ToolPermissionMemory(),
            new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            composer: Composer, promptOptions: options);

    private static FakeModelCatalog CatalogWithSpriteModel(int workbenchMaxOutput = 1024) => new()
    {
        Current = new AppSettings(
            [FakeModelCatalog.Connection],
            [FakeModelCatalog.Model with { MaxOutputTokens = workbenchMaxOutput }, SpriteModel],
            FakeModelCatalog.Model.Id, SpriteModel.Id, ThemePreference.System),
    };

    [Fact]
    public async Task Request_CarriesSystemAndModeSuffix_ButHistoryDoesNot()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog()).SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(provider.Requests);
        Assert.StartsWith("你是 Lumi", request.System);
        Assert.StartsWith("<lumi_mode>\n目前模式：工作台", request.TurnSuffix);
        Assert.Null(request.MaxOutputTokens);
        Assert.Equal(FakeModelCatalog.Model.MaxOutputTokens, request.EffectiveMaxOutputTokens);

        // 模式指示只存在請求裡：送出的訊息和存檔都沒有它（M5 的 Q2）
        Assert.DoesNotContain(request.Messages.SelectMany(m => m.Content).OfType<TextBlock>(), t => t.Text.Contains("lumi_mode"));
        var stored = await store.GetMessagesAsync(conversation.Id);
        Assert.DoesNotContain(stored.SelectMany(m => m.Content).OfType<TextBlock>(), t => t.Text.Contains("lumi_mode"));
    }

    [Fact]
    public async Task SwitchingMode_KeepsSystemAndTools_ChangesOnlySuffixModelAndCap()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var catalog = new FakeModelCatalog();   // 兩邊同一個模型：快取才可能共用
        var service = Service(provider, store, catalog, tools: [new FakeTool("bench_only", modes: ToolModes.Workbench)]);

        await Collect(service.SendAsync(conversation.Id, "嗨", mode: ChatMode.Workbench));
        await Collect(service.SendAsync(conversation.Id, "再一次", mode: ChatMode.Sprite));

        var (bench, sprite) = (provider.Requests[0], provider.Requests[1]);
        Assert.Equal(bench.System, sprite.System);
        Assert.Equal(bench.Tools!.Select(t => t.Descriptor.Name), sprite.Tools!.Select(t => t.Descriptor.Name));
        Assert.Equal(bench.Model, sprite.Model);
        Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", sprite.TurnSuffix);
        // 第二次請求的歷史就是第一次的歷史往後加：前綴沒有被改動
        Assert.Equal(bench.Messages.Select(m => m.Id), sprite.Messages.Take(bench.Messages.Count).Select(m => m.Id));
    }

    [Fact]
    public async Task SpriteMode_UsesSpriteModel_WithLowerCap()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, CatalogWithSpriteModel()).SendAsync(conversation.Id, "嗨", mode: ChatMode.Sprite));

        var request = Assert.Single(provider.Requests);
        Assert.Equal(SpriteModel, request.Model.Model);
        Assert.Equal(2048, request.MaxOutputTokens);   // min(8000, 預設 2048)
        Assert.Equal(2048, request.EffectiveMaxOutputTokens);
    }

    [Fact]
    public async Task SpriteCap_NeverRaisesTheModelLimit()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), new PromptOptions { SpriteMaxOutputTokens = 4096 })
            .SendAsync(conversation.Id, "嗨", mode: ChatMode.Sprite));

        Assert.Equal(FakeModelCatalog.Model.MaxOutputTokens, Assert.Single(provider.Requests).MaxOutputTokens);   // 1024 < 4096
    }

    [Fact]
    public async Task SpriteMode_FallsBackToWorkbenchModel_WhenSpriteModelIsMissing()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var catalog = new FakeModelCatalog();
        catalog.Current = catalog.Current with { SpriteModelId = "gone" };

        await Collect(Service(provider, store, catalog).SendAsync(conversation.Id, "嗨", mode: ChatMode.Sprite));

        Assert.Equal(FakeModelCatalog.Model, Assert.Single(provider.Requests).Model.Model);
    }

    [Fact]
    public async Task WorkbenchMode_IgnoresSpriteModel()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, CatalogWithSpriteModel()).SendAsync(conversation.Id, "嗨"));

        Assert.Equal(FakeModelCatalog.Model, Assert.Single(provider.Requests).Model.Model);
    }

    [Fact]
    public async Task PersonaChange_IsPickedUpOnTheNextRequest()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var catalog = new FakeModelCatalog();
        var service = Service(provider, store, catalog);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        await catalog.SetPersonaAsync(new PersonaSettings(PersonaStyle.Professional, Proactivity.Reserved, "叫我 Neil"));
        await Collect(service.SendAsync(conversation.Id, "再一次"));

        Assert.DoesNotContain("叫我 Neil", provider.Requests[0].System);
        Assert.Contains("專業精簡", provider.Requests[1].System);
        Assert.Contains("叫我 Neil", provider.Requests[1].System);
    }

    [Fact]
    public async Task ToolLoop_EveryCallInTheTurnCarriesTheSuffix()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "echo", """{"text":"嗨"}""")), SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), tools: [new FakeTool()]).SendAsync(conversation.Id, "嗨", mode: ChatMode.Sprite));

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, r => Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", r.TurnSuffix));
        Assert.Equal(ChatRole.Tool, provider.Requests[1].Messages[^1].Role);
    }

    [Fact]
    public async Task WithoutComposer_RequestHasNoPrompt()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(new ConversationService(provider, store, new FakeModelCatalog()).SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(provider.Requests);
        Assert.Null(request.System);
        Assert.Null(request.TurnSuffix);
    }
}
