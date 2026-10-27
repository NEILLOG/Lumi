using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>M22 22J：單一 ChatSession 不變，切模式時切對話——小精靈用自己的專屬對話，回到工作台開回原本那則。</summary>
public class SpriteConversationSwitcherTests
{
    private sealed class Rig
    {
        public FakeWindowHost Host { get; } = new();
        public FakeModelCatalog Settings { get; } = new();
        public InMemoryConversationStore Store { get; } = new();
        public SpriteConversationService Sprite { get; }
        public ChatSession Session { get; }
        public SpriteConversationSwitcher Switcher { get; }
        public AppModeCoordinator Coordinator { get; }
        public ConversationService Service { get; }

        public Rig(IChatProvider? provider = null, ChatMode lastMode = ChatMode.Workbench)
        {
            Settings.Current = Settings.Current with { Sprite = SpriteSettings.Default with { LastMode = lastMode } };
            Sprite = new SpriteConversationService(Store);
            Service = new ConversationService(
                provider ?? new ScriptedChatProvider(new Completed(ScriptedChatProvider.Assistant(new TextBlock("好")), StopReason.EndTurn)),
                Store, new FakeModelCatalog());
            Session = new ChatSession(Service, Store, sprite: Sprite);
            Switcher = new SpriteConversationSwitcher(Session, Sprite, Store, NullLogger<SpriteConversationSwitcher>.Instance);
            Coordinator = new AppModeCoordinator(Session, Host, Settings, new SpriteShellOptions(), new ManualTimeProvider(),
                NullLogger<AppModeCoordinator>.Instance, Switcher);
        }

        public async Task<Conversation> SeedAsync(string title)
        {
            await Task.Delay(3);
            var conversation = await Store.CreateAsync(title);
            await Store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(title)], DateTimeOffset.UtcNow));
            return conversation;
        }

        public async Task<string> SpriteIdAsync() => (await Sprite.GetIdAsync())!;
    }

    private static IChatProvider Hanging() => new ScriptedChatProvider(new TextDelta("…")) { HangAfter = 1 };

    [Fact]
    public async Task EnteringSpriteMode_OpensTheDedicatedConversation_CreatedWithTheStandardTitle()
    {
        var rig = new Rig();
        var work = await rig.Session.InitializeAsync("新對話");

        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();

        var spriteId = await rig.SpriteIdAsync();
        Assert.NotEqual(work.Id, spriteId);
        Assert.Equal(spriteId, rig.Session.ConversationId);
        Assert.Equal(Strings.Sprite_ConversationTitle, (await rig.Store.GetConversationAsync(spriteId))!.Title);
        Assert.Equal(spriteId, await rig.Store.GetMetaAsync("sprite_conversation_id"));
    }

    [Fact]
    public async Task BackToTheWorkbench_ReopensTheConversationItWasOn()
    {
        var rig = new Rig();
        var work = await rig.SeedAsync("工作台的對話");
        await rig.Session.InitializeAsync("新對話");
        Assert.Equal(work.Id, rig.Session.ConversationId);
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();

        rig.Coordinator.OpenWorkbench();
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);
        Assert.Equal(work.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task TheSpriteConversation_IsTheSameOneEachTime_AndKeepsItsMessages()
    {
        var rig = new Rig();
        await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        var spriteId = rig.Session.ConversationId!;
        await rig.Session.SendAsync("你好");
        rig.Coordinator.OpenWorkbench();
        await rig.Switcher.WhenIdleAsync();

        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(spriteId, rig.Session.ConversationId);
        Assert.Equal(2, (await rig.Store.GetMessagesAsync(spriteId)).Count);   // 使用者與助理各一則
    }

    [Fact]
    public async Task AWorkbenchSend_DoesNotLandInTheSpriteConversation()
    {
        var rig = new Rig();
        var work = await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        rig.Coordinator.OpenWorkbench();
        await rig.Switcher.WhenIdleAsync();

        await rig.Session.SendAsync("在工作台說的話");

        Assert.Equal(2, (await rig.Store.GetMessagesAsync(work.Id)).Count);
        Assert.Empty(await rig.Store.GetMessagesAsync(await rig.SpriteIdAsync()));
    }

    [Fact]
    public async Task WhenTheRememberedConversationWasDeleted_ItFallsBackToTheLatestOtherOne_NotTheSpriteConversation()
    {
        var rig = new Rig();
        var older = await rig.SeedAsync("較舊的對話");
        var current = await rig.SeedAsync("目前的對話");
        await rig.Session.InitializeAsync("新對話");
        Assert.Equal(current.Id, rig.Session.ConversationId);
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        await rig.Store.DeleteAsync(current.Id);

        rig.Coordinator.OpenWorkbench();
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(older.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task WhenNothingElseIsLeft_ReturningStartsANewEmptyWorkbenchConversation()
    {
        var rig = new Rig();
        var current = await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        await rig.Store.DeleteAsync(current.Id);

        rig.Coordinator.OpenWorkbench();
        await rig.Switcher.WhenIdleAsync();

        Assert.NotEqual(await rig.SpriteIdAsync(), rig.Session.ConversationId);
        Assert.NotEqual(current.Id, rig.Session.ConversationId);
        Assert.Equal(Strings.Workbench_NewConversationTitle, (await rig.Store.GetConversationAsync(rig.Session.ConversationId!))!.Title);
    }

    [Fact]
    public async Task WhenTheSpriteConversationWasDeleted_TheNextEntryRecreatesIt()
    {
        var rig = new Rig();
        await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        var firstId = rig.Session.ConversationId!;
        rig.Coordinator.OpenWorkbench();
        await rig.Switcher.WhenIdleAsync();
        await rig.Store.DeleteAsync(firstId);

        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();

        Assert.NotEqual(firstId, rig.Session.ConversationId);
        Assert.Equal(rig.Session.ConversationId, await rig.SpriteIdAsync());
        Assert.Equal(Strings.Sprite_ConversationTitle, (await rig.Store.GetConversationAsync(rig.Session.ConversationId!))!.Title);
    }

    // ---- 啟動時的模式 ----

    [Fact]
    public async Task StartingInSpriteMode_OpensTheSpriteConversation_NotTheLatestWorkbenchOne()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);
        var work = await rig.SeedAsync("工作台的對話");
        await rig.Session.InitializeAsync("新對話");   // 啟動：App.StartAsync 先載入工作台的對話

        rig.Coordinator.Start();
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);
        Assert.Equal(await rig.SpriteIdAsync(), rig.Session.ConversationId);
        Assert.NotEqual(work.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task StartingInWorkbenchMode_OpensTheLatestWorkbenchConversation_EvenIfTheSpriteOneIsNewer()
    {
        var rig = new Rig();
        var work = await rig.SeedAsync("工作台的對話");
        var spriteConversation = await rig.Sprite.GetOrCreateAsync(Strings.Sprite_ConversationTitle);
        await rig.Store.AppendMessageAsync(spriteConversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("嗨")], DateTimeOffset.UtcNow));

        await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.Start();
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);
        Assert.Equal(work.Id, rig.Session.ConversationId);
    }

    // ---- 回應中切換延後 ----

    [Fact]
    public async Task WhenTheModeChangesWhileResponding_TheSwitchWaitsForTheRunToFinish()
    {
        var fake = new FakeChatSession { ConversationId = "work", IsResponding = true };
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var switcher = new SpriteConversationSwitcher(fake, sprite, store, NullLogger<SpriteConversationSwitcher>.Instance);

        fake.SetMode(ChatMode.Sprite);
        await switcher.WhenIdleAsync();
        Assert.Empty(fake.Opened);          // 回應還沒結束：不切、也不停止
        Assert.Equal("work", fake.ConversationId);

        fake.FinishRun();
        await switcher.WhenIdleAsync();

        Assert.Equal([(await sprite.GetIdAsync())!], fake.Opened);
    }

    [Fact]
    public async Task OpeningTheWorkbenchWhileTheSpriteIsResponding_ViewsTheRun_AndReturnsToTheWorkbenchConversationAfterwards()
    {
        var rig = new Rig(Hanging());
        var work = await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        var spriteId = rig.Session.ConversationId!;
        var run = rig.Session.SendAsync("嗨");
        await SettingsHost.WaitUntil(() => rig.Session.IsResponding);

        rig.Coordinator.OpenWorkbench();   // 查看中：模式與對話都還是小精靈的
        await rig.Switcher.WhenIdleAsync();
        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);
        Assert.Equal(spriteId, rig.Session.ConversationId);

        rig.Session.Stop();
        await run;
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);
        Assert.Equal(work.Id, rig.Session.ConversationId);
    }

    // ---- 在工作台顯示完整內容 ----

    [Fact]
    public async Task OpeningTheSpriteBubbleInTheWorkbench_ShowsTheSpriteConversation_AndScrollsToTheCall()
    {
        var rig = new Rig();
        var work = await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        var spriteId = rig.Session.ConversationId!;

        rig.Coordinator.OpenWorkbenchForSpriteConversation("call-1");
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);
        Assert.Equal(spriteId, rig.Session.ConversationId);   // 工作台顯示小精靈的對話，不切回原本的
        Assert.Contains("scroll:call-1", rig.Host.Log);

        // 之後再進出小精靈模式：「回到工作台」仍然是原本那則
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        rig.Coordinator.OpenWorkbench();
        await rig.Switcher.WhenIdleAsync();
        Assert.Equal(work.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task APlainOpenWorkbench_GoesBackToTheWorkbenchConversation_NotTheSpriteOne()
    {
        var rig = new Rig();
        var work = await rig.Session.InitializeAsync("新對話");
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();

        rig.Coordinator.OpenWorkbench();   // 托盤、右鍵選單的「開啟工作台」
        await rig.Switcher.WhenIdleAsync();

        Assert.Equal(work.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task ShowDetailsCard_OfTheSpriteConversation_IsLoadedInTheWorkbenchWhenOpenedFromTheBubble()
    {
        var rig = new Rig();
        var catalog = new FakeModelCatalog();
        var vm = new WorkbenchViewModel(rig.Service, rig.Store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: rig.Session, modes: rig.Coordinator, spriteConversation: rig.Sprite);
        await vm.InitializeAsync();
        var work = rig.Session.ConversationId!;

        // 小精靈對話裡有一張 show_details 卡
        var spriteConversation = await rig.Sprite.GetOrCreateAsync(Strings.Sprite_ConversationTitle);
        var detailsInput = """{"title":"範例","markdown":"完整內容"}""";
        await rig.Store.AppendMessageAsync(spriteConversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("給我範例")], DateTimeOffset.UtcNow));
        await rig.Store.AppendMessageAsync(spriteConversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new ToolUseBlock("details-1", "show_details", detailsInput)], DateTimeOffset.UtcNow));
        await rig.Store.AppendMessageAsync(spriteConversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
            [new ToolResultBlock("details-1", "ok", false, "整理完整內容", "範例", null, 1, ToolApproval.NotRequired)], DateTimeOffset.UtcNow));
        rig.Coordinator.EnterSpriteMode();
        await rig.Switcher.WhenIdleAsync();
        await SettingsHost.WaitUntil(() => vm.Messages.Count > 0);

        rig.Coordinator.OpenWorkbenchForSpriteConversation("details-1");
        await rig.Switcher.WhenIdleAsync();

        Assert.NotEqual(work, rig.Session.ConversationId);
        var card = vm.Messages.OfType<ToolRunViewModel>().SelectMany(r => r.Cards).Single(c => c.CallId == "details-1");
        Assert.True(card.IsDetails);
        Assert.Equal("完整內容", card.DetailsMarkdown);
        Assert.Contains("scroll:details-1", rig.Host.Log);
    }
}
