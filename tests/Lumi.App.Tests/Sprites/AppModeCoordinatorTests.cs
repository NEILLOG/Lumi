using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>plan/07「模式與視窗」表的每一列。</summary>
public class AppModeCoordinatorTests
{
    private sealed class Rig
    {
        public FakeWindowHost Host { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public FakeModelCatalog Settings { get; } = new();
        public ChatSession Session { get; }
        public AppModeCoordinator Coordinator { get; }

        public Rig(IChatProvider? provider = null, ChatMode lastMode = ChatMode.Workbench)
        {
            Settings.Current = Settings.Current with { Sprite = SpriteSettings.Default with { LastMode = lastMode } };
            var store = new InMemoryConversationStore();
            var service = new ConversationService(provider ?? new ScriptedChatProvider(new Completed(ScriptedChatProvider.Assistant(new TextBlock("好")), StopReason.EndTurn)),
                store, new FakeModelCatalog());
            Session = new ChatSession(service, store);
            Coordinator = new AppModeCoordinator(Session, Host, Settings, new SpriteShellOptions(), Time, NullLogger<AppModeCoordinator>.Instance);
        }

        public async Task<Task<ChatRunResult>> StartHangingRunAsync()
        {
            await Session.InitializeAsync("新對話");
            return Session.SendAsync("嗨");
        }
    }

    private static IChatProvider Hanging() => new ScriptedChatProvider(new TextDelta("…")) { HangAfter = 1 };

    // ---- 啟動 ----

    [Fact]
    public void Start_FirstTime_ShowsTheWorkbench()
    {
        var rig = new Rig();

        rig.Coordinator.Start();

        Assert.Equal(["showWorkbench"], rig.Host.Log);
        Assert.Equal(ChatMode.Workbench, rig.Coordinator.ActiveMode);
    }

    [Fact]
    public void Start_AfterQuittingInSpriteMode_ReturnsToSpriteMode_WithoutTheWorkbench()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);

        rig.Coordinator.Start();

        Assert.Equal(["showSprite"], rig.Host.Log);
        Assert.Equal(ChatMode.Sprite, rig.Coordinator.ActiveMode);
        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);
    }

    // ---- 工作台按「小精靈模式」 ----

    [Fact]
    public async Task EnteringSpriteMode_HidesTheWorkbench_ShowsTheSprite_AndRemembersIt()
    {
        var rig = new Rig();
        rig.Coordinator.Start();

        var entered = rig.Coordinator.EnterSpriteMode();
        await SettingsHost.WaitUntil(() => rig.Settings.Current.Sprite.LastMode == ChatMode.Sprite);

        Assert.True(entered);
        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);
        Assert.False(rig.Host.IsWorkbenchVisible);
        Assert.True(rig.Host.IsSpriteVisible);
    }

    [Fact]
    public async Task EnteringSpriteMode_WhileResponding_IsRefused_AndNothingMoves()
    {
        var rig = new Rig(Hanging());
        var run = await rig.StartHangingRunAsync();
        rig.Coordinator.Start();
        rig.Host.Log.Clear();

        var entered = rig.Coordinator.EnterSpriteMode();

        Assert.False(entered);
        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);
        Assert.Empty(rig.Host.Log);
        await rig.Session.StopAndWaitAsync();
        await run;
    }

    // ---- 小精靈開啟工作台 ----

    [Fact]
    public void OpeningTheWorkbenchFromTheSprite_WhenIdle_LeavesBothOpen_AndTheModeAlone()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);
        rig.Coordinator.Start();

        rig.Coordinator.OpenWorkbench();

        Assert.True(rig.Host.IsWorkbenchVisible);
        Assert.True(rig.Host.IsSpriteVisible);
        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);
    }

    [Fact]
    public async Task OpeningTheWorkbenchWhileTheSpriteIsAnswering_IsViewOnly_ThatIsAnActiveSpriteModeRun()
    {
        var rig = new Rig(Hanging(), ChatMode.Sprite);
        rig.Coordinator.Start();
        var run = await rig.StartHangingRunAsync();

        rig.Coordinator.OpenWorkbench();

        Assert.True(rig.Host.IsWorkbenchVisible);
        Assert.True(rig.Session.IsResponding);
        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);   // 工作台據此推導「查看中」
        Assert.False(rig.Session.TrySetMode(ChatMode.Workbench));   // 回應中不能切換
        await rig.Session.StopAndWaitAsync();
        await run;
        Assert.False(rig.Session.IsResponding);   // 結束後自動解除
    }

    [Fact]
    public void OpeningTheWorkbenchAtAToolCard_ScrollsToIt()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);
        rig.Coordinator.Start();

        rig.Coordinator.OpenWorkbench("call-7");

        Assert.Equal(["showSprite", "showWorkbench", "scroll:call-7"], rig.Host.Log);
    }

    // ---- 在工作台送出 ----

    [Fact]
    public async Task SendingFromTheWorkbench_SwitchesToWorkbenchMode_AndHidesTheSprite()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);
        rig.Coordinator.Start();
        rig.Coordinator.OpenWorkbench();   // 兩個視窗同時開著

        rig.Coordinator.WorkbenchWillSend();
        await SettingsHost.WaitUntil(() => rig.Settings.Current.Sprite.LastMode == ChatMode.Workbench);

        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);
        Assert.False(rig.Host.IsSpriteVisible);
        Assert.True(rig.Host.IsWorkbenchVisible);
    }

    [Fact]
    public void SendingFromTheWorkbench_InWorkbenchMode_DoesNothing()
    {
        var rig = new Rig();
        rig.Coordinator.Start();
        rig.Host.Log.Clear();

        rig.Coordinator.WorkbenchWillSend();

        Assert.Empty(rig.Host.Log);
    }

    // ---- 關閉工作台 ----

    [Fact]
    public void ClosingTheWorkbench_InWorkbenchMode_QuitsTheApp()
    {
        var rig = new Rig();
        rig.Coordinator.Start();

        var close = rig.Coordinator.WorkbenchMayClose();

        Assert.True(close);
        Assert.True(rig.Host.Exited);
    }

    [Fact]
    public void ClosingTheWorkbench_InSpriteMode_OnlyClosesThatWindow()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);
        rig.Coordinator.Start();
        rig.Coordinator.OpenWorkbench();

        var close = rig.Coordinator.WorkbenchMayClose();

        Assert.False(close);   // 視窗改成隱藏，不結束
        Assert.False(rig.Host.Exited);
    }

    // ---- 暫時隱藏與托盤 ----

    [Fact]
    public async Task HidingTemporarily_ShowsTheSpriteAgainAfterThirtyMinutes()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);
        rig.Coordinator.Start();

        rig.Coordinator.HideSpriteTemporarily();
        Assert.False(rig.Host.IsSpriteVisible);
        Assert.True(rig.Coordinator.IsSpriteTemporarilyHidden);

        rig.Time.Advance(TimeSpan.FromMinutes(29));
        await Task.Delay(30);
        Assert.False(rig.Host.IsSpriteVisible);
        Assert.True(rig.Coordinator.IsSpriteTemporarilyHidden);

        rig.Time.Advance(TimeSpan.FromMinutes(2));
        await SettingsHost.WaitUntil(() => rig.Host.IsSpriteVisible);
        Assert.False(rig.Coordinator.IsSpriteTemporarilyHidden);
    }

    [Fact]
    public async Task TheTrayCanEndTheTemporaryHideEarly_AndTheOldTimerDoesNothingLater()
    {
        var rig = new Rig(lastMode: ChatMode.Sprite);
        rig.Coordinator.Start();
        rig.Coordinator.HideSpriteTemporarily();

        rig.Coordinator.ShowSprite();
        Assert.True(rig.Host.IsSpriteVisible);
        Assert.False(rig.Coordinator.IsSpriteTemporarilyHidden);

        rig.Host.Log.Clear();
        rig.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Empty(rig.Host.Log);   // 被取消的計時器不會再動作
    }

    [Fact]
    public void TheTrayShowSprite_InWorkbenchMode_SwitchesToSpriteMode()
    {
        var rig = new Rig();
        rig.Coordinator.Start();

        rig.Coordinator.ShowSprite();

        Assert.Equal(ChatMode.Sprite, rig.Session.ActiveMode);
        Assert.True(rig.Host.IsSpriteVisible);
        Assert.False(rig.Host.IsWorkbenchVisible);
    }

    [Fact]
    public async Task AnApprovalWhileTheSpriteIsHidden_ShowsItAutomatically()
    {
        // 真的跑一輪需要確認的工具呼叫：對話迴圈會停在確認上，直到有人回答
        var store = new InMemoryConversationStore();
        var approvals = new ToolApprovalService();
        var tool = new LambdaTool("save", "儲存", ToolPermission.Write);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, new FakeModelCatalog(), new ToolRegistry([tool]), approvals,
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance);
        var session = new ChatSession(service, store);
        var host = new FakeWindowHost();
        var settings = new FakeModelCatalog();
        settings.Current = settings.Current with { Sprite = SpriteSettings.Default with { LastMode = ChatMode.Sprite } };
        var coordinator = new AppModeCoordinator(session, host, settings, new SpriteShellOptions(), new FakeTimeProvider(), NullLogger<AppModeCoordinator>.Instance);
        coordinator.Start();
        await session.InitializeAsync("新對話");
        coordinator.HideSpriteTemporarily();
        Assert.False(host.IsSpriteVisible);

        var run = session.SendAsync("存");
        await SettingsHost.WaitUntil(() => host.IsSpriteVisible);   // 需要確認時自動顯示，不然對話迴圈會一直等

        Assert.False(coordinator.IsSpriteTemporarilyHidden);
        approvals.Resolve("t1", ApprovalDecision.AllowOnce);
        await run;
    }

    [Fact]
    public void ExitGoesThroughTheHost()
    {
        var rig = new Rig();

        rig.Coordinator.Exit();

        Assert.True(rig.Host.Exited);
    }
}
