using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Tools;
using Lumi.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>5C：工作台的 show_details 內容卡，以及開發用的「小精靈模式」切換。</summary>
public class WorkbenchDetailsAndModeTests
{
    private const string DetailsJson = """{"title":"C# 讀檔範例","markdown":"## 範例\n```csharp\nFile.ReadAllText(path);\n```"}""";

    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new ModeFormatPromptLayer(), new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
    ]);

    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private sealed record Rig(WorkbenchViewModel Vm, InMemoryConversationStore Store, SequenceChatProvider Provider, FakeModelCatalog Catalog, ChatSession Session);

    private static Rig CreateRig(SequenceChatProvider provider, InMemoryConversationStore? store = null)
    {
        store ??= new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var time = new FakeTimeProvider();
        var service = new ConversationService(provider, store, catalog, new ToolRegistry([new ShowDetailsTool()]), new ToolApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance, composer: Composer);
        var session = new ChatSession(service, store);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
            session: session);
        return new Rig(vm, store, provider, catalog, session);
    }

    private static SequenceChatProvider DetailsThenAnswer() => new(
        SequenceChatProvider.ToolCalls(null, new ToolUseBlock("d1", ShowDetailsTool.Name, DetailsJson)),
        SequenceChatProvider.Answer("範例放在工作台了。"));

    private static async Task Send(Rig rig, string text = "寫一段 C# 讀檔範例")
    {
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = text;
        await rig.Vm.SendCommand.ExecuteAsync(null);
    }

    private static ToolCardViewModel OnlyCard(Rig rig) => rig.Vm.Messages.OfType<ToolRunViewModel>().Single().Cards.Single();

    // ---- 內容卡 ----

    [Fact]
    public async Task ShowDetails_InSpriteMode_BecomesAnOpenContentCard()
    {
        var rig = CreateRig(DetailsThenAnswer());
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));

        rig.Vm.InputText = "寫一段 C# 讀檔範例";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        var card = OnlyCard(rig);
        Assert.True(card.IsDetails);
        Assert.True(card.ShowDetailsCard);
        Assert.False(card.ShowRegularCard);
        Assert.Equal("整理完整內容 · C# 讀檔範例", card.DetailsHeader);
        Assert.StartsWith("## 範例", card.DetailsMarkdown);
        Assert.Contains("File.ReadAllText", card.DetailsMarkdown);
        Assert.Equal($"{card.DetailsMarkdown.Length} 字", card.DetailsCharacters);
        Assert.Equal("範例放在工作台了。", rig.Vm.Messages[^1].Text);   // 之後還有一句話的回答
        Assert.All(rig.Provider.Requests, r => Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", r.TurnSuffix));
    }

    [Fact]
    public async Task ShowDetails_ContentCardSurvivesReopeningTheConversation()
    {
        var rig = CreateRig(DetailsThenAnswer());
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));
        rig.Vm.InputText = "x";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        // 重開程式：同一個資料庫、全新的 ViewModel
        var reopened = CreateRig(new SequenceChatProvider(), store: rig.Store);
        await reopened.Vm.InitializeAsync();

        var card = OnlyCard(reopened);
        Assert.True(card.ShowDetailsCard);
        Assert.Equal("整理完整內容 · C# 讀檔範例", card.DetailsHeader);
        Assert.Equal(OnlyCard(rig).DetailsMarkdown, card.DetailsMarkdown);
    }

    [Fact]
    public async Task ShowDetails_InWorkbenchMode_IsAnOrdinaryFailedCard()
    {
        var rig = CreateRig(DetailsThenAnswer());

        await Send(rig);   // 沒有打開小精靈模式

        var card = OnlyCard(rig);
        Assert.True(card.IsDetails);
        Assert.Equal(ToolCardState.Failed, card.State);
        Assert.False(card.ShowDetailsCard);
        Assert.True(card.ShowRegularCard);   // 看得到參數與錯誤，不會顯示內容卡
        Assert.Contains("目前模式", card.ResultText);
        Assert.StartsWith("<lumi_mode>\n目前模式：工作台", rig.Provider.Requests[0].TurnSuffix);
    }

    [Fact]
    public void DetailsCard_FallsBackToTheRegularCard_WhenArgumentsAreBroken()
    {
        var card = new ToolCardViewModel("d1", ShowDetailsTool.Name, "整理完整內容", null, null, "{ broken",
            new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());
        card.Complete(new ToolResultBlock("d1", "ok", false, "整理完整內容", null, "0 字", 1, ToolApproval.NotRequired));

        Assert.True(card.IsDetails);
        Assert.False(card.ShowDetailsCard);   // 沒有內容可以顯示
    }

    [Fact]
    public async Task DetailsCard_CopiesTheWholeMarkdown_NotTheAcknowledgement()
    {
        var clipboard = new FakeClipboardService();
        var card = new ToolCardViewModel("d1", ShowDetailsTool.Name, "整理完整內容", null, null, DetailsJson,
            new NoopResponder(), clipboard, new FakeTimeProvider());
        card.Complete(new ToolResultBlock("d1", "已放到工作台，使用者可以點開查看。", false, "整理完整內容", "C# 讀檔範例", "40 字", 1, ToolApproval.NotRequired));

        await card.CopyResultCommand.ExecuteAsync(null);

        Assert.Equal(card.DetailsMarkdown, Assert.Single(clipboard.Texts));
    }

    [Fact]
    public void ContentCards_DoNotCountTowardsGrouping()
    {
        ToolCardViewModel Card(string id, string tool) => new(id, tool, tool, null, null, DetailsJson,
            new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());

        var run = new ToolRunViewModel();
        run.Cards.Add(Card("a", "read_file"));
        run.Cards.Add(Card("b", "read_file"));
        run.Cards.Add(Card("d", ShowDetailsTool.Name));
        Assert.False(run.IsGroup);   // 兩個一般呼叫＋一張內容卡：不合併

        run.Cards.Add(Card("c", "search"));
        Assert.True(run.IsGroup);
    }

    // ---- 模式（由會話決定；工作台與小精靈共用） ----

    [Fact]
    public async Task TheMode_DefaultsToWorkbench_AndSpriteModeSendsTheSpriteSuffix()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("好"));
        var rig = CreateRig(provider);
        await rig.Vm.InitializeAsync();
        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);

        rig.Vm.InputText = "一";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));
        rig.Vm.InputText = "二";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        Assert.StartsWith("<lumi_mode>\n目前模式：工作台", provider.Requests[0].TurnSuffix);
        Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", provider.Requests[1].TurnSuffix);
        Assert.Equal(provider.Requests[0].System, provider.Requests[1].System);   // 切換模式不改 system
        Assert.Null(provider.Requests[0].MaxOutputTokens);
        Assert.Equal(1024, provider.Requests[1].MaxOutputTokens);   // min(模型設定 1024, 2048)
    }

    [Fact]
    public async Task ModeCannotBeSwitchedWhileResponding()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 };
        var rig = CreateRig(provider);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "x";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding && provider.Calls == 1);

        Assert.False(rig.Session.TrySetMode(ChatMode.Sprite));   // 回應中（ui-spec 2.2）：被拒絕
        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);

        await rig.Vm.StopAndWaitAsync();
        await sending;
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));
    }

    [Fact]
    public async Task RegenerateAndRetry_KeepTheCurrentMode()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var rig = CreateRig(provider);
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));
        rig.Vm.InputText = "x";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, r => Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", r.TurnSuffix));
    }
}
