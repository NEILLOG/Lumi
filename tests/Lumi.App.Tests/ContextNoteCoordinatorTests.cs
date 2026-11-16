using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;

namespace Lumi.App.Tests;

/// <summary>M35 35J：研究本協調者（會話事件 → 研究本服務與視窗）。</summary>
public class ContextNoteCoordinatorTests
{
    private sealed record Rig(FakeChatSession Session, FakeContextNoteService Notes, FakeContextNoteWindowService Window, ContextNoteCoordinator Coordinator);

    private static Rig Create()
    {
        var session = new FakeChatSession { ConversationId = "conv-1" };
        var notes = new FakeContextNoteService();
        var window = new FakeContextNoteWindowService();
        var coordinator = new ContextNoteCoordinator(session, notes, window, new SyncUiDispatcher());
        return new Rig(session, notes, window, coordinator);
    }

    [Fact]
    public void SuccessfulRun_NotifiesTheTurnFinished()
    {
        var rig = Create();
        rig.Session.StartRun();

        rig.Session.FinishRun();

        Assert.Equal(["NotifyTurnFinished:conv-1"], rig.Notes.Calls);
    }

    [Fact]
    public void FailedRun_IsNotNotified()
    {
        var rig = Create();
        rig.Session.StartRun();

        rig.Session.FinishRun(new ChatRunResult("conv-1", new ChatFailure(ChatFailureKind.ModelNotConfigured, "連線失敗", Retryable: false), false, false));

        Assert.Empty(rig.Notes.Calls);
    }

    [Fact]
    public void StoppedRun_IsNotNotified()
    {
        var rig = Create();
        rig.Session.StartRun();

        rig.Session.FinishRun(new ChatRunResult("conv-1", null, false, true));

        Assert.Empty(rig.Notes.Calls);
    }

    [Fact]
    public void UnexpectedRun_IsNotNotified()
    {
        var rig = Create();
        rig.Session.StartRun();

        rig.Session.FinishRun(new ChatRunResult("conv-1", null, true, false));

        Assert.Empty(rig.Notes.Calls);
    }

    [Fact]
    public void ChangingConversation_MakesTheWindowFollow()
    {
        var rig = Create();

        rig.Session.SwitchConversation("conv-2");

        Assert.Equal(["Follow:conv-2"], rig.Window.Calls);
    }

    [Fact]
    public void SwitchingToSpriteMode_ClosesTheWindow()
    {
        var rig = Create();

        rig.Session.SetMode(ChatMode.Sprite);

        Assert.Equal(["Close"], rig.Window.Calls);
    }

    [Fact]
    public void SwitchingToWorkbenchMode_DoesNotCloseTheWindow()
    {
        var rig = Create();

        rig.Session.SetMode(ChatMode.Workbench);

        Assert.Empty(rig.Window.Calls);
    }

    [Fact]
    public void AfterDispose_NothingIsForwarded()
    {
        var rig = Create();

        rig.Coordinator.Dispose();
        rig.Session.StartRun();
        rig.Session.FinishRun();
        rig.Session.SwitchConversation("conv-2");
        rig.Session.SetMode(ChatMode.Sprite);

        Assert.Empty(rig.Notes.Calls);
        Assert.Empty(rig.Window.Calls);
    }
}
