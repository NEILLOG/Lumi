using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M24 包 24F：工作台的排程紀錄檢視（打開、關閉、即時更新、說明列）、接著聊與引用卡。</summary>
public class WorkbenchScheduledRunTests
{
    private sealed class RecordingModes : IAppModeCoordinator
    {
        public ChatMode ActiveMode => ChatMode.Workbench;
        public bool IsSpriteTemporarilyHidden => false;
        public int OpenWorkbenchCalls { get; private set; }
        public void Start() { }
        public bool EnterSpriteMode() => true;
        public void OpenWorkbench(string? scrollToCallId = null) => OpenWorkbenchCalls++;
        public void WorkbenchWillSend() { }
        public bool WorkbenchMayClose() => false;
        public void ShowSprite() { }
        public void HideSpriteTemporarily() { }
        public void Exit() { }
    }

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public InMemoryScheduledRunStore Runs { get; } = new();
        public FakeScheduledRunService Service { get; } = new();
        public InMemoryConversationContextStore Context { get; } = new();
        public RecordingModes Modes { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public ContextBarViewModel Bar { get; }
        public WorkbenchViewModel Vm { get; }

        public Rig(IChatSession? session = null, IChatProvider? provider = null)
        {
            var catalog = new FakeModelCatalog();
            var builder = new ContextAttachmentBuilder(new FileToolOptions(), new FakeAttachmentStore(), new HeuristicTokenEstimator(), null, Runs, Store);
            var service = new ConversationService(provider ?? new SequenceChatProvider(SequenceChatProvider.Answer("好")), Store, catalog,
                new ToolRegistry([]), new ToolApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(), Time,
                NullLogger<ConversationService>.Instance, Context, new EmptyFileScopeFactory(), builder);
            Bar = new ContextBarViewModel(Context, Store, new FakeContextInspector(), builder, new HeuristicTokenEstimator(), new FakeFilePicker(),
                new FakeClipboardImage(), new FakeAttachmentStore(), Time, Runs);
            Vm = new WorkbenchViewModel(service, Store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), Time, NullLogger<WorkbenchViewModel>.Instance,
                contextBar: Bar, session: session, modes: Modes, scheduledRuns: Service, runStore: Runs,
                runLogger: NullLogger<ScheduledRunViewModel>.Instance);
        }
    }

    private static readonly DateTimeOffset Planned = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    /// <summary>建立一則排程紀錄（與它的對話：使用者的 prompt＋助理的回答）。</summary>
    private static async Task<ScheduledRun> SeedRunAsync(
        Rig rig, ScheduledRunStatus status = ScheduledRunStatus.Completed, bool withConversation = true,
        string answer = "今天的重點是三件事。", string? failure = null, ScheduledRunTrigger trigger = ScheduledRunTrigger.Scheduled)
    {
        string? conversationId = null;
        if (withConversation)
        {
            var conversation = await rig.Store.CreateAsync("每日摘要");
            conversationId = conversation.Id;
            await rig.Store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("整理今天的新聞")], Planned));
            if (answer.Length > 0)
                await rig.Store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(answer)], Planned));
        }
        var started = status is ScheduledRunStatus.Missed ? null : (DateTimeOffset?)Planned;
        var run = new ScheduledRun(ScheduledRun.NewId(), "schedule-1", "每日摘要", conversationId, Planned, started,
            status is ScheduledRunStatus.Running or ScheduledRunStatus.AwaitingApproval ? null : started, trigger, status, failure, null);
        await rig.Runs.AddAsync(run);
        return run;
    }

    private static string? LastText(WorkbenchViewModel vm) => vm.ShownMessages.OfType<MessageViewModel>().LastOrDefault(m => m.IsAssistant)?.Text;

    // ---- 整合：歷史抽屜的排程分頁、待確認的紀錄 ----

    [Fact]
    public async Task Open_AwaitingApprovalRun_RestoresTheConfirmButtons_AndDoesNotMarkTheCallAsNoResult()
    {
        var rig = new Rig();
        var conversation = await rig.Store.CreateAsync("每日摘要");
        await rig.Store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("整理")], Planned));
        await rig.Store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new ToolUseBlock("call-1", "write_file", "{}")], Planned));
        var run = new ScheduledRun(ScheduledRun.NewId(), "schedule-1", "每日摘要", conversation.Id, Planned, Planned, null,
            ScheduledRunTrigger.Scheduled, ScheduledRunStatus.AwaitingApproval, null, null);
        await rig.Runs.AddAsync(run);
        rig.Service.Pending[run.Id] = new ToolApprovalRequest("call-1", "write_file", "寫入檔案", ToolPermission.Write, new ToolPreview("a.md", null), "{}");

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        var card = rig.Vm.ShownMessages.OfType<ToolRunViewModel>().Single().Cards.Single();
        Assert.True(card.IsAwaiting);
        Assert.True(card.IsScheduledRun);
    }

    // ---- 打開與標已讀 ----

    [Fact]
    public async Task Open_ShowsTheRunsMessages_BadgesThePrompt_AndMarksRead()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        Assert.NotNull(rig.Vm.ViewingRun);
        Assert.True(rig.Vm.IsViewingRun);
        var shown = rig.Vm.ShownMessages.ToList();
        Assert.Equal(2, shown.Count);
        Assert.True(shown[0].IsUser);
        Assert.Equal("整理今天的新聞", shown[0].Text);
        Assert.True(shown[0].ShowScheduleBadge);
        Assert.Equal("今天的重點是三件事。", shown[1].Text);
        Assert.Equal([run.Id], rig.Service.ReadRuns);
    }

    [Fact]
    public async Task Open_DoesNotChangeTheCurrentConversation_OrItsResponse()
    {
        var session = new FakeChatSession { ConversationId = "chat-now" };
        var rig = new Rig(session);
        session.StartRun();
        var run = await SeedRunAsync(rig);
        var before = rig.Vm.Messages.Count;

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        Assert.NotNull(rig.Vm.ViewingRun);
        Assert.True(session.IsResponding);
        Assert.Equal("chat-now", session.ConversationId);
        Assert.Equal(before, rig.Vm.Messages.Count);   // 目前對話的訊息不動（回應的氣泡還在背後）
    }

    [Fact]
    public async Task Open_InSpriteMode_WhileResponding_DoesNothing()
    {
        var session = new FakeChatSession { ActiveMode = ChatMode.Sprite };
        var rig = new Rig(session);
        session.StartRun();
        var run = await SeedRunAsync(rig);

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        Assert.Null(rig.Vm.ViewingRun);
        Assert.Empty(rig.Service.ReadRuns);
        Assert.Equal(0, rig.Modes.OpenWorkbenchCalls);
    }

    [Fact]
    public async Task Open_InSpriteMode_NotResponding_OpensTheWorkbenchFirst()
    {
        var session = new FakeChatSession { ActiveMode = ChatMode.Sprite };
        var rig = new Rig(session);
        var run = await SeedRunAsync(rig);

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        Assert.Equal(1, rig.Modes.OpenWorkbenchCalls);
        Assert.NotNull(rig.Vm.ViewingRun);
    }

    [Fact]
    public async Task Open_MissingRun_DoesNothing()
    {
        var rig = new Rig();

        await rig.Vm.OpenScheduledRunAsync("不存在");

        Assert.Null(rig.Vm.ViewingRun);
        Assert.Empty(rig.Service.ReadRuns);
    }

    // ---- 關閉 ----

    [Fact]
    public async Task BackToChat_ClosesTheRunView()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        rig.Vm.BackToChatCommand.Execute(null);

        Assert.Null(rig.Vm.ViewingRun);
        Assert.False(rig.Vm.IsViewingRun);
    }

    [Fact]
    public async Task NewConversation_ClosesTheRunView()
    {
        var rig = new Rig(new FakeChatSession());
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        Assert.Null(rig.Vm.ViewingRun);
    }

    [Fact]
    public async Task OpenAnotherConversation_ClosesTheRunView()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        var other = await rig.Store.CreateAsync("別的對話");
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        await rig.Vm.OpenConversationAsync(other);

        Assert.Null(rig.Vm.ViewingRun);
    }

    [Fact]
    public async Task OpenSettings_ClosesTheRunView()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        rig.Vm.OpenSettingsCommand.Execute(null);

        Assert.Null(rig.Vm.ViewingRun);
    }

    // ---- 說明列與按鈕（各狀態） ----

    [Fact]
    public async Task Completed_CanContinue_ButNotRunAgainOrStop()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        var view = rig.Vm.ViewingRun!;
        Assert.True(view.CanContinue);
        Assert.True(view.ShowBarNote);
        Assert.False(view.CanRunAgain);
        Assert.False(view.CanStop);
        Assert.False(view.HasStatusNote);
    }

    [Fact]
    public async Task MissedRun_HasNoConversation_ShowsMissedNote_AndRunAgain_WithoutContinue()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Missed, withConversation: false);

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        var view = rig.Vm.ViewingRun!;
        Assert.Empty(rig.Vm.ShownMessages);
        Assert.Equal(Strings.Schedule_MissedNote, view.StatusNote);
        Assert.True(view.CanRunAgain);
        Assert.False(view.CanContinue);
        Assert.False(view.ShowBarNote);
    }

    [Fact]
    public async Task FailedRun_ShowsTheFailureReason_AndRunAgain()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Failed, failure: "供應商暫時不可用");

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        var view = rig.Vm.ViewingRun!;
        Assert.Equal(string.Format(Strings.Schedule_FailedNoteFormat, "供應商暫時不可用"), view.StatusNote);
        Assert.True(view.CanRunAgain);
        Assert.True(view.CanContinue);
    }

    [Fact]
    public async Task InterruptedRun_ShowsTheInterruptedNote_AndRunAgain()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Interrupted);

        await rig.Vm.OpenScheduledRunAsync(run.Id);

        Assert.Equal(Strings.Schedule_InterruptedNote, rig.Vm.ViewingRun!.StatusNote);
        Assert.True(rig.Vm.ViewingRun.CanRunAgain);
    }

    [Fact]
    public async Task RunAgain_StartsTheSameSchedule()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Missed, withConversation: false);
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        await rig.Vm.RunViewAgainCommand.ExecuteAsync(null);

        Assert.Equal(["schedule-1"], rig.Service.RunNowCalls);
    }

    [Fact]
    public async Task RunningRun_CanStop_AndStopCallsTheService()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Running);
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        Assert.True(rig.Vm.ViewingRun!.CanStop);
        rig.Vm.StopViewingRunCommand.Execute(null);

        Assert.Equal([run.Id], rig.Service.StopCalls);
    }

    [Fact]
    public async Task Banner_NamesTheRun_AndMarksCatchUpAndManual()
    {
        var rig = new Rig();
        var catchUp = await SeedRunAsync(rig, trigger: ScheduledRunTrigger.CatchUp);
        await rig.Vm.OpenScheduledRunAsync(catchUp.Id);
        Assert.Contains("每日摘要", rig.Vm.ViewingRun!.BannerText);
        Assert.Contains(Strings.Schedule_Trigger_CatchUp, rig.Vm.ViewingRun.BannerText);

        var manual = await SeedRunAsync(rig, trigger: ScheduledRunTrigger.Manual);
        await rig.Vm.OpenScheduledRunAsync(manual.Id);
        Assert.Contains(Strings.Schedule_Trigger_Manual, rig.Vm.ViewingRun!.BannerText);
    }

    // ---- 即時更新 ----

    [Fact]
    public async Task RunningRun_LiveEvents_AppendToTheRunView_NotTheCurrentConversation()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Running, answer: "");
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        rig.Service.Raise(new ScheduledRunEventArgs(run.Id, run.ConversationId!, new TextDelta("正在")));
        rig.Service.Raise(new ScheduledRunEventArgs(run.Id, run.ConversationId!, new TextDelta("整理")));

        Assert.Equal("正在整理", LastText(rig.Vm));
        Assert.Empty(rig.Vm.Messages);   // 目前的對話沒有被動到
    }

    [Fact]
    public async Task RunningRun_EventsForOtherRuns_AreIgnored()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Running, answer: "");
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        rig.Service.Raise(new ScheduledRunEventArgs("別的執行", "x", new TextDelta("不該出現")));

        Assert.DoesNotContain("不該出現", rig.Vm.ShownMessages.Select(m => m.Text));
    }

    [Fact]
    public async Task RunningRun_ToolCalls_BecomeScheduledRunCards()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Running, answer: "");
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        rig.Service.Raise(new ScheduledRunEventArgs(run.Id, run.ConversationId!,
            new ToolCallStarted("call-1", "web_search", "搜尋網頁", ToolPermission.ReadOnly, "新聞", "{}")));

        var tools = rig.Vm.ShownMessages.OfType<ToolRunViewModel>().Single();
        var card = Assert.Single(tools.Cards);
        Assert.True(card.IsScheduledRun);
        Assert.Contains("搜尋網頁", card.Title);
    }

    [Fact]
    public async Task RunFinished_ReloadsTheStoredConversation()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Running, answer: "最終回答");
        await rig.Vm.OpenScheduledRunAsync(run.Id);
        rig.Service.Raise(new ScheduledRunEventArgs(run.Id, run.ConversationId!, new TextDelta("半")));

        rig.Service.Raise(run with { Status = ScheduledRunStatus.Completed, FinishedAt = Planned });

        Assert.Equal("最終回答", LastText(rig.Vm));
        Assert.DoesNotContain(rig.Vm.ShownMessages, m => m.IsStreaming);
        Assert.False(rig.Vm.ViewingRun!.CanStop);
    }

    [Fact]
    public async Task Close_UnsubscribesFromTheService()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig, ScheduledRunStatus.Running, answer: "");
        await rig.Vm.OpenScheduledRunAsync(run.Id);
        var view = rig.Vm.ViewingRun!;

        rig.Vm.BackToChatCommand.Execute(null);
        var before = view.Messages.Count;
        rig.Service.Raise(new ScheduledRunEventArgs(run.Id, run.ConversationId!, new TextDelta("關掉之後")));

        Assert.Equal(before, view.Messages.Count);
        Assert.DoesNotContain("關掉之後", view.Messages.Select(m => m.Text));
    }

    // ---- 接著聊與引用卡 ----

    [Fact]
    public async Task ContinueChat_OpensANewChat_AddsTheQuoteCard_AndClosesTheRunView()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);

        await rig.Vm.ContinueFromRunCommand.ExecuteAsync(null);

        Assert.Null(rig.Vm.ViewingRun);
        Assert.Equal(string.Format(Strings.Schedule_ContinueChatTitleFormat, "每日摘要"), rig.Vm.Title);
        var chip = Assert.Single(rig.Bar.Items);
        Assert.True(chip.IsScheduledRun);
        Assert.Equal(run.Id, chip.Path);
        Assert.Equal(string.Format(Strings.Schedule_QuoteTitleFormat, "每日摘要", ScheduledRunContext.FormatTime(Planned)), chip.QuoteText);
        Assert.False(chip.IsRunGone);
    }

    [Fact]
    public async Task FirstSend_AfterContinue_PutsTheAnswerIntoTheMessage_AndShowsTheQuoteOnTheBubble()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);
        await rig.Vm.ContinueFromRunCommand.ExecuteAsync(null);

        rig.Vm.InputText = "那第二件事呢？";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        var quote = Assert.Single(rig.Vm.Messages.First(m => m.IsUser).Attachments);
        Assert.True(quote.IsScheduledRun);
        Assert.Equal(run.Id, quote.RunId);
        Assert.False(quote.IsRunGone);
        Assert.Equal(Strings.Schedule_QuoteViewOriginal, quote.ViewOriginalText);

        // 送給模型的內容：快照（名稱、時間、最後的回答）放在使用者訊息的區塊裡（第一則送出後對話標題改成訊息文字）
        var conversation = (await rig.Store.ListAsync(10)).First(c => c.Title == "那第二件事呢？");
        var stored = (await rig.Store.GetMessagesAsync(conversation.Id)).First(m => m.Role == ChatRole.User);
        var block = Assert.IsType<ContextBlock>(Assert.Single(stored.Content.OfType<ContextBlock>()));
        Assert.Equal(ContextKind.ScheduledRun, block.Kind);
        Assert.True(block.Embedded);
        Assert.Contains("今天的重點是三件事。", block.Content);
        Assert.Contains("每日摘要", block.Content);
    }

    [Fact]
    public async Task OpenOriginal_FromTheBubble_OpensTheRun()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);
        await rig.Vm.ContinueFromRunCommand.ExecuteAsync(null);
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        var quote = rig.Vm.Messages.First(m => m.IsUser).Attachments.Single();

        await rig.Vm.OpenQuotedRunCommand.ExecuteAsync(quote);

        Assert.NotNull(rig.Vm.ViewingRun);
        Assert.Equal(run.Id, rig.Vm.ViewingRun!.RunId);
    }

    [Fact]
    public async Task QuoteWhoseRunIsGone_IsDisabled_AndDoesNotOpen()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);
        await rig.Vm.ContinueFromRunCommand.ExecuteAsync(null);
        await rig.Runs.DeleteAsync(run.Id);

        // 紀錄在送出前被清掉：沒有東西可以引用，引用卡停用
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        // 重讀對話（先離開再回來）：引用卡依存下來的區塊重建，送出時的樣子是樂觀的
        await rig.Vm.OpenConversationAsync(await rig.Store.CreateAsync("別的對話"));
        await rig.Vm.OpenConversationAsync((await rig.Store.ListAsync(10)).First(c => c.Title == "問"));
        var quote = rig.Vm.Messages.First(m => m.IsUser).Attachments.Single();

        Assert.True(quote.IsRunGone);
        Assert.Equal(Strings.Schedule_QuoteGone, quote.ViewOriginalText);
        await rig.Vm.OpenQuotedRunCommand.ExecuteAsync(quote);
        Assert.Null(rig.Vm.ViewingRun);
    }

    [Fact]
    public async Task ContextChip_ForAGoneRun_ShowsTheGoneText()
    {
        var rig = new Rig();
        var run = await SeedRunAsync(rig);
        await rig.Vm.OpenScheduledRunAsync(run.Id);
        await rig.Vm.ContinueFromRunCommand.ExecuteAsync(null);
        await rig.Runs.DeleteAsync(run.Id);

        await rig.Bar.RefreshAsync();

        var chip = Assert.Single(rig.Bar.Items);
        Assert.True(chip.IsRunGone);
        Assert.Contains(Strings.Schedule_QuoteGone, chip.QuoteText);
        Assert.Null(chip.Detail);
    }
}
