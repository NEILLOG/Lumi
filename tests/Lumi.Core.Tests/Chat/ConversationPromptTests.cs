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
        Assert.Equal(bench.Tools!.Select(t => t.Name), sprite.Tools!.Select(t => t.Name));
        Assert.Equal(bench.Model, sprite.Model);
        Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", sprite.TurnSuffix);
        // 第二次請求的歷史就是第一次的歷史往後加：前綴沒有被改動
        Assert.Equal(bench.Messages.Select(m => m.Id), sprite.Messages.Take(bench.Messages.Count).Select(m => m.Id));
    }

    [Fact]
    public async Task SpriteMode_UsesTheWorkbenchModel_IgnoringTheOldSpriteModel_WithLowerCap()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, CatalogWithSpriteModel(workbenchMaxOutput: 8000)).SendAsync(conversation.Id, "嗨", mode: ChatMode.Sprite));

        var request = Assert.Single(provider.Requests);
        Assert.Equal(FakeModelCatalog.Model.Id, request.Model.Model.Id);   // 小精靈不另外選模型
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
    public async Task SpriteMode_UsesTheWorkbenchModel_EvenWithAStaleSpriteModelId()
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
    public async Task PersonaChange_IsPickedUpOnTheNextRequest_ThroughAnUpdateBlock()
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
        // system 在一則對話內固定；新的人設改以更新區塊附在下一則訊息後面
        Assert.Equal(provider.Requests[0].System, provider.Requests[1].System);
        var update = SystemPromptUpdate.LastIn([provider.Requests[1].TurnSuffix]);
        Assert.Contains("專業精簡", update);
        Assert.Contains("叫我 Neil", update);
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
    public async Task TurnSuffix_IsStoredOnTheLastMessage_AndReplayedUnchangedInLaterRequests_EvenAfterAModeSwitch()
    {
        // thinking 簽章綁定它之前的全部內容：第一次請求附在最後一則的指示，之後每次請求都要原樣在那一則裡
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"), SequenceChatProvider.Answer("三"));
        var service = Service(provider, store, new FakeModelCatalog());

        await Collect(service.SendAsync(conversation.Id, "第一則", mode: ChatMode.Workbench));
        await Collect(service.SendAsync(conversation.Id, "第二則", mode: ChatMode.Sprite));
        await Collect(service.SendAsync(conversation.Id, "第三則", mode: ChatMode.Workbench));

        var first = provider.Requests[0].TurnSuffix;
        var second = provider.Requests[1].TurnSuffix;
        Assert.NotEqual(first, second);   // 兩種模式的指示不同
        Assert.StartsWith("<lumi_mode>\n目前模式：工作台", first);

        var stored = (await store.GetMessagesAsync(conversation.Id)).Where(m => m.Role == ChatRole.User).ToList();
        Assert.Equal([first, second, provider.Requests[2].TurnSuffix], stored.Select(m => m.TurnSuffix));

        // 第三次請求裡，前兩則使用者訊息還帶著當初送出時的指示
        var users = provider.Requests[2].Messages.Where(m => m.Role == ChatRole.User).ToList();
        Assert.Equal([first, second, provider.Requests[2].TurnSuffix], users.Select(m => m.TurnSuffix));
        // 助理訊息本身沒有
        Assert.All(provider.Requests[2].Messages.Where(m => m.Role == ChatRole.Assistant), m => Assert.Null(m.TurnSuffix));
    }

    [Fact]
    public async Task TurnSuffix_InAToolLoop_StaysOnTheUserMessageAndTheToolResultMessage()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "echo", """{"text":"嗨"}""")),
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t2", "echo", """{"text":"再"}""")),
            SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), tools: [new FakeTool()]).SendAsync(conversation.Id, "嗨", mode: ChatMode.Sprite));

        // 第三次請求：使用者訊息、第一個工具結果訊息都還帶著當時的指示（第二個工具結果是最後一則，帶這一次的）
        var messages = provider.Requests[2].Messages;
        Assert.All(messages.Where(m => m.Role is ChatRole.User or ChatRole.Tool), m => Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", m.TurnSuffix));
        Assert.Equal(2, messages.Count(m => m.Role == ChatRole.Tool));
    }

    [Fact]
    public async Task Retry_ReplacesTheLastMessagesSuffix_BecauseNothingIsBoundToItYet()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(
            [new Failed(new ChatFailure(ChatFailureKind.Network, "斷線", Retryable: true))], SequenceChatProvider.Answer("好"));
        var service = Service(provider, store, new FakeModelCatalog());

        await Collect(service.SendAsync(conversation.Id, "嗨", mode: ChatMode.Workbench));
        await Collect(service.RetryAsync(conversation.Id, mode: ChatMode.Sprite));

        var user = Assert.Single(await store.GetMessagesAsync(conversation.Id), m => m.Role == ChatRole.User);
        Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", user.TurnSuffix);
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
