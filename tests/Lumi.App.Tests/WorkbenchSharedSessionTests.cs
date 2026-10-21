using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>
/// 7A：工作台是 <see cref="IChatSession"/> 的觀察者。用「直接對會話送出」模擬小精靈：
/// 工作台沒有參與送出，仍要看到同一則對話、同一份事件，並同步權限確認。
/// </summary>
public class WorkbenchSharedSessionTests
{
    private sealed record Rig(WorkbenchViewModel Vm, ChatSession Session, InMemoryConversationStore Store, ToolApprovalService Approvals, LambdaTool Tool);

    private static Rig CreateRig(IChatProvider provider, IApiKeyPrompt? prompt = null, ToolPermission permission = ToolPermission.Write)
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var approvals = new ToolApprovalService();
        var time = new FakeTimeProvider();
        var tool = new LambdaTool("save", "儲存", permission);
        var service = new ConversationService(provider, store, catalog, new ToolRegistry([tool]), approvals, new ToolPermissionMemory(),
            new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance);
        var session = new ChatSession(service, store);
        var vm = new WorkbenchViewModel(service, store, catalog, prompt ?? new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
            approvals, session: session);
        return new Rig(vm, session, store, approvals, tool);
    }

    private static Task Wait(Func<bool> condition) => SettingsHost.WaitUntil(condition);

    private static ToolCardViewModel OnlyCard(Rig rig) => rig.Vm.Messages.OfType<ToolRunViewModel>().Single().Cards.Single();

    [Fact]
    public async Task ARunStartedElsewhere_ShowsTheQuestionAndTheAnswerInTheWorkbench()
    {
        var rig = CreateRig(new SequenceChatProvider(SequenceChatProvider.Answer("台北是晴天")));
        await rig.Vm.InitializeAsync();

        await rig.Session.SendAsync("今天天氣");

        Assert.Equal(["今天天氣", "台北是晴天"], rig.Vm.Messages.Select(m => m.Text));
        Assert.True(rig.Vm.Messages[0].IsUser);
        Assert.False(rig.Vm.IsResponding);
        Assert.Equal(1, rig.Vm.Turns);
        Assert.Equal("今天天氣", rig.Vm.Title);
    }

    [Fact]
    public async Task WhileARunStartedElsewhereIsInProgress_TheWorkbenchIsResponding()
    {
        var rig = CreateRig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        await rig.Vm.InitializeAsync();

        var sending = rig.Session.SendAsync("嗨");
        await Wait(() => rig.Vm.IsResponding);

        Assert.False(rig.Vm.CanSwitchModel);
        rig.Vm.InputText = "另一則";
        Assert.False(rig.Vm.SendCommand.CanExecute(null));   // 不能同時送出兩輪

        await rig.Session.StopAndWaitAsync();
        await sending;
        Assert.False(rig.Vm.IsResponding);
    }

    [Fact]
    public async Task ApprovalAnsweredElsewhere_UpdatesTheWorkbenchCard_AndTheRunContinues()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("存好了"));
        var rig = CreateRig(provider);
        await rig.Vm.InitializeAsync();

        var sending = rig.Session.SendAsync("存");
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        var card = OnlyCard(rig);

        rig.Approvals.Resolve("t1", ApprovalDecision.AllowOnce);   // 小精靈的氣泡按了「允許」
        await sending;

        Assert.Equal(1, rig.Tool.Runs);
        Assert.Equal(ToolCardState.Succeeded, card.State);
        Assert.Equal(ToolApproval.AllowedOnce, card.Approval);
        Assert.Equal("存好了", rig.Vm.Messages[^1].Text);
    }

    [Fact]
    public async Task ApprovalAnsweredElsewhere_ShowsRunningImmediately_BeforeTheToolFinishes()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("好")) { HangOnCall = 2 };
        var rig = CreateRig(provider);
        await rig.Vm.InitializeAsync();
        var sending = rig.Session.SendAsync("存");
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        var card = OnlyCard(rig);

        rig.Approvals.Resolve("t1", ApprovalDecision.AllowForConversation);
        await Wait(() => provider.Calls == 2);   // 工具跑完、第二次呼叫供應商時卡片已經離開等待狀態

        Assert.False(card.IsAwaiting);
        Assert.Equal(ToolApproval.AllowedForConversation, card.Approval);
        await rig.Session.StopAndWaitAsync();
        await sending;
    }

    [Fact]
    public async Task DenyAnsweredElsewhere_ShowsDeniedOnTheWorkbenchCard()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("好吧"));
        var rig = CreateRig(provider);
        await rig.Vm.InitializeAsync();
        var sending = rig.Session.SendAsync("存");
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        var card = OnlyCard(rig);

        rig.Approvals.Resolve("t1", ApprovalDecision.Deny);
        await sending;

        Assert.Equal(0, rig.Tool.Runs);
        Assert.Equal(ToolCardState.Denied, card.State);
        Assert.Equal(ToolApproval.Denied, card.Approval);
    }

    [Fact]
    public async Task ApprovalAnsweredOnTheCard_StillWorks_AndIsResolvedOnlyOnce()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("好"));
        var rig = CreateRig(provider);
        var resolved = new List<ApprovalResolved>();
        rig.Approvals.Resolved += (_, r) => resolved.Add(r);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "存";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        OnlyCard(rig).AllowOnceCommand.Execute(null);
        await sending;

        Assert.Equal([new ApprovalResolved("t1", ApprovalDecision.AllowOnce)], resolved);
        Assert.Equal(ToolApproval.AllowedOnce, OnlyCard(rig).Approval);
    }

    [Fact]
    public async Task AFailureElsewhere_ShowsTheErrorBar_WithoutPromptingForAKey()
    {
        var failure = new ChatFailure(ChatFailureKind.MissingApiKey, "缺少金鑰", Retryable: false);
        var prompt = new FakeApiKeyPrompt(true);
        var rig = CreateRig(new ScriptedChatProvider(new Failed(failure)), prompt);
        await rig.Vm.InitializeAsync();

        await rig.Session.SendAsync("嗨");

        Assert.Equal("缺少金鑰", rig.Vm.ErrorMessage);
        Assert.Empty(prompt.Calls);   // 金鑰提示與重送由發起的那一邊處理
    }

    [Fact]
    public async Task ModeChangedOnTheSession_TellsTheWorkbenchToRecomputeViewOnly()
    {
        var rig = CreateRig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();
        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Session.TrySetMode(ChatMode.Sprite);

        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);
        Assert.Contains(nameof(WorkbenchViewModel.IsViewOnly), changed);
    }

    [Fact]
    public async Task ANewConversationElsewhere_IsShownByTheWorkbench()
    {
        var rig = CreateRig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();
        await rig.Session.SendAsync("第一則");
        Assert.Equal(2, rig.Vm.Messages.Count);

        var created = await rig.Session.NewConversationAsync("新對話");
        await Wait(() => rig.Vm.Messages.Count == 0);

        Assert.Equal(created.Title, rig.Vm.Title);
        Assert.Equal(created.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task ANewConversationFromTheWorkbench_IsLoadedOnce()
    {
        var rig = CreateRig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "第一則";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        Assert.Empty(rig.Vm.Messages);
        Assert.Equal(rig.Session.ConversationId, (await rig.Store.GetLatestAsync())!.Id);
    }

    // ---- 卡片 ----

    [Fact]
    public void Card_ApplyResolved_IsIgnoredUnlessAwaiting()
    {
        var card = new ToolCardViewModel("c1", "save", "儲存", ToolPermission.Write, "目標", "{}", new NoopResponder(),
            new FakeClipboardService(), new FakeTimeProvider());

        card.ApplyResolved(ApprovalDecision.Deny);   // 還在執行中，沒有人在等

        Assert.Equal(ToolCardState.Running, card.State);
        Assert.Equal(ToolApproval.NotRequired, card.Approval);
    }

    [Fact]
    public void ToolApprovalService_RaisesResolved_ForEveryAnswer()
    {
        var service = new ToolApprovalService();
        var seen = new List<ApprovalResolved>();
        service.Resolved += (_, r) => seen.Add(r);

        service.Resolve("a", ApprovalDecision.Deny);
        service.Resolve("b", ApprovalDecision.AllowOnce);

        Assert.Equal([new ApprovalResolved("a", ApprovalDecision.Deny), new ApprovalResolved("b", ApprovalDecision.AllowOnce)], seen);
    }

    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }
}
