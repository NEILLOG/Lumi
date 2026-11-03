using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>工作台角色的對話接線（plan/27 第 8.3 節）：第 5.5 節的狀態表、跳與探頭的觸發條件。</summary>
public class WorkbenchSpriteControllerTests
{
    /// <summary>手動觸發事件的假會話；訂閱數量可以查，用來確認 Attach 不會重複訂閱。</summary>
    private sealed class ControllerSession : IChatSession
    {
        private EventHandler<RunStartedArgs>? _runStarted;
        private EventHandler<ChatEvent>? _eventReceived;
        private EventHandler<RunFinishedArgs>? _runFinished;
        private EventHandler<ConversationChangedArgs>? _conversationChanged;

        public string? ConversationId { get; set; } = "conv";

        public ChatMode ActiveMode { get; set; } = ChatMode.Workbench;

        public bool IsResponding { get; set; }

        public int RunStartedSubscriptions => _runStarted?.GetInvocationList().Length ?? 0;

        public event EventHandler<ConversationChangedArgs>? ConversationChanged
        {
            add => _conversationChanged += value;
            remove => _conversationChanged -= value;
        }

        public event EventHandler<RunStartedArgs>? RunStarted
        {
            add => _runStarted += value;
            remove => _runStarted -= value;
        }

        public event EventHandler<ChatEvent>? EventReceived
        {
            add => _eventReceived += value;
            remove => _eventReceived -= value;
        }

        public event EventHandler<RunFinishedArgs>? RunFinished
        {
            add => _runFinished += value;
            remove => _runFinished -= value;
        }

        public event EventHandler<ChatMode>? ModeChanged
        {
            add { }
            remove { }
        }

        /// <summary>模擬一輪回應開始。</summary>
        public void Start(ChatRunKind kind = ChatRunKind.Send, ChatMode mode = ChatMode.Workbench)
        {
            IsResponding = true;
            _runStarted?.Invoke(this, new RunStartedArgs(ConversationId!, mode, kind, kind == ChatRunKind.Send ? "你好" : null));
        }

        public void Emit(ChatEvent chatEvent) => _eventReceived?.Invoke(this, chatEvent);

        public void Finish(ChatRunResult? result = null)
        {
            IsResponding = false;
            _runFinished?.Invoke(this, new RunFinishedArgs(result ?? Ev.Finished()));
        }

        public void Switch(string conversationId)
        {
            ConversationId = conversationId;
            var now = DateTimeOffset.UnixEpoch;
            _conversationChanged?.Invoke(this, new ConversationChangedArgs(new Conversation(conversationId, "對話", now, now)));
        }

        public Task<Conversation> InitializeAsync(string newConversationTitle, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Conversation> OpenConversationAsync(Conversation conversation) => throw new NotSupportedException();
        public Task<Conversation> NewConversationAsync(string title) => throw new NotSupportedException();
        public Task<ChatRunResult> SendAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatRunResult> RetryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatRunResult> RegenerateAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CompactionResult> CompactAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Stop() { }
        public Task StopAndWaitAsync() => Task.CompletedTask;
        public bool TrySetMode(ChatMode mode) => false;
    }

    /// <summary>可以從測試端觸發 <see cref="IToolApprovalResponder.Resolved"/> 的假回答者。</summary>
    private sealed class ResolvingResponder : IToolApprovalResponder
    {
        public event EventHandler<ApprovalResolved>? Resolved;

        public void Resolve(string callId, ApprovalDecision decision) => Resolved?.Invoke(this, new ApprovalResolved(callId, decision));
    }

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new();
        public ControllerSession Session { get; } = new();
        public ResolvingResponder Approvals { get; } = new();
        public List<SpriteState> States { get; } = [];
        public WorkbenchSpriteController Controller { get; }

        public Rig(WorkbenchSpriteSlot slot = WorkbenchSpriteSlot.Toolbar, bool attach = true)
        {
            Controller = new WorkbenchSpriteController(Session, Approvals, new SpriteShellOptions(), Time);
            Controller.StateChanged += States.Add;
            Controller.Slot = slot;
            if (attach)
                Controller.Attach();
        }

        public void Advance(TimeSpan by) => Time.Advance(by);

        public void Dispose() => Controller.Dispose();
    }

    // ---- 第 5.5 節狀態表 ----

    [Fact]
    public void RunStarted_IsThinking()
    {
        using var rig = new Rig();

        rig.Session.Start();

        Assert.Equal(SpriteState.Thinking, rig.Controller.State);
    }

    [Fact]
    public void TextDelta_IsStreaming()
    {
        using var rig = new Rig();
        rig.Session.Start();

        rig.Session.Emit(new TextDelta("嗨"));

        Assert.Equal(SpriteState.Streaming, rig.Controller.State);
    }

    [Fact]
    public void ToolCall_IsTool_AndCompletedGoesBackToThinking()
    {
        using var rig = new Rig();
        rig.Session.Start();

        rig.Session.Emit(Ev.Tool());
        Assert.Equal(SpriteState.Tool, rig.Controller.State);

        rig.Session.Emit(Ev.ToolDone());
        Assert.Equal(SpriteState.Thinking, rig.Controller.State);
    }

    [Fact]
    public void ApprovalRequired_IsAttention_AndAnsweredGoesBackToTool()
    {
        using var rig = new Rig();
        rig.Session.Start();

        rig.Session.Emit(Ev.Approval("c1"));
        Assert.Equal(SpriteState.Attention, rig.Controller.State);

        rig.Approvals.Resolve("c1", ApprovalDecision.AllowOnce);
        Assert.Equal(SpriteState.Tool, rig.Controller.State);
    }

    [Fact]
    public void ApprovalResolvedForAnotherCall_IsIgnored()
    {
        using var rig = new Rig();
        rig.Session.Start();
        rig.Session.Emit(Ev.Approval("c1"));

        rig.Approvals.Resolve("other", ApprovalDecision.Deny);

        Assert.Equal(SpriteState.Attention, rig.Controller.State);
    }

    [Fact]
    public void Completed_IsDone_AndAfterDoneHoldIsIdle()
    {
        using var rig = new Rig();
        rig.Session.Start();
        rig.Session.Emit(Ev.Done());
        rig.Session.Finish();

        Assert.Equal(SpriteState.Done, rig.Controller.State);

        rig.Advance(TimeSpan.FromSeconds(3));
        rig.Controller.Tick();

        Assert.Equal(SpriteState.Idle, rig.Controller.State);
    }

    [Fact]
    public void Failed_IsError_AndAConversationChangeLeavesIt()
    {
        using var rig = new Rig();
        rig.Session.Start();
        rig.Session.Emit(new Failed(Ev.Failure()));
        rig.Session.Finish(Ev.Finished(Ev.Failure()));

        Assert.Equal(SpriteState.Error, rig.Controller.State);

        rig.Session.Switch("conv-2");

        Assert.Equal(SpriteState.Idle, rig.Controller.State);
    }

    [Fact]
    public void Idle_AutoSleepsAfterTheSleepDelay()
    {
        using var rig = new Rig();

        rig.Advance(TimeSpan.FromSeconds(61));
        rig.Controller.Tick();

        Assert.Equal(SpriteState.Sleep, rig.Controller.State);
    }

    [Fact]
    public void NearWakesASleepingSprite()
    {
        using var rig = new Rig();
        rig.Advance(TimeSpan.FromSeconds(61));
        rig.Controller.Tick();
        Assert.Equal(SpriteState.Sleep, rig.Controller.State);

        rig.Controller.SetNear(true);

        Assert.Equal(SpriteState.Idle, rig.Controller.State);
    }

    [Fact]
    public void Touch_RestartsTheSleepTimer()
    {
        using var rig = new Rig();
        rig.Advance(TimeSpan.FromSeconds(59));

        rig.Controller.Touch();
        rig.Advance(TimeSpan.FromSeconds(2));
        rig.Controller.Tick();

        Assert.Equal(SpriteState.Idle, rig.Controller.State);
    }

    // ---- 模式與接線 ----

    [Fact]
    public void SpriteModeRuns_AreReflectedToo()
    {
        using var rig = new Rig();

        rig.Session.Start(ChatRunKind.Send, ChatMode.Sprite);

        Assert.Equal(SpriteState.Thinking, rig.Controller.State);
    }

    [Fact]
    public void BeforeAttach_EventsAreIgnored()
    {
        using var rig = new Rig(attach: false);

        rig.Session.Start();

        Assert.Equal(SpriteState.Idle, rig.Controller.State);
    }

    [Fact]
    public void AfterDetach_EventsAreIgnored()
    {
        using var rig = new Rig();
        rig.Controller.Detach();
        rig.Controller.Detach();

        rig.Session.Start();

        Assert.Equal(SpriteState.Idle, rig.Controller.State);
    }

    [Fact]
    public void Attach_TwiceSubscribesOnce()
    {
        using var rig = new Rig();

        rig.Controller.Attach();

        Assert.Equal(1, rig.Session.RunStartedSubscriptions);
    }

    [Fact]
    public void Attach_WhileResponding_IsThinking()
    {
        using var rig = new Rig(attach: false);
        rig.Session.IsResponding = true;

        rig.Controller.Attach();

        Assert.Equal(SpriteState.Thinking, rig.Controller.State);
    }

    [Fact]
    public void Dispose_Detaches()
    {
        var rig = new Rig();

        rig.Controller.Dispose();
        rig.Session.Start();

        Assert.Equal(SpriteState.Idle, rig.Controller.State);
    }

    // ---- 跳一下 ----

    [Fact]
    public void Send_OnToolbar_Jumps()
    {
        using var rig = new Rig();

        rig.Session.Start(ChatRunKind.Send);

        Assert.True(rig.Controller.Motion.IsAnimating);
    }

    [Fact]
    public void Retry_DoesNotJump()
    {
        using var rig = new Rig();

        rig.Session.Start(ChatRunKind.Retry);

        Assert.False(rig.Controller.Motion.IsAnimating);
    }

    [Fact]
    public void Regenerate_DoesNotJump()
    {
        using var rig = new Rig();

        rig.Session.Start(ChatRunKind.Regenerate);

        Assert.False(rig.Controller.Motion.IsAnimating);
    }

    [Fact]
    public void Send_OnWelcome_DoesNotJump()
    {
        using var rig = new Rig(WorkbenchSpriteSlot.Welcome);

        rig.Session.Start(ChatRunKind.Send);

        Assert.False(rig.Controller.Motion.IsAnimating);
    }

    [Fact]
    public void Send_RightAfterTeleport_DoesNotJump_ButAfterOneSecondDoes()
    {
        using var rig = new Rig();
        rig.Controller.Motion.BeginTeleport(() => { });
        rig.Controller.Motion.Update();
        rig.Advance(TimeSpan.FromMilliseconds(300));
        rig.Controller.Motion.Update();
        Assert.False(rig.Controller.Motion.IsTeleporting);

        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.Session.Start(ChatRunKind.Send);
        Assert.False(rig.Controller.Motion.IsAnimating);
        rig.Session.Finish();

        rig.Advance(TimeSpan.FromMilliseconds(600));
        rig.Session.Start(ChatRunKind.Send);
        Assert.True(rig.Controller.Motion.IsAnimating);
    }

    [Fact]
    public void Send_WhileTeleporting_DoesNotJump()
    {
        using var rig = new Rig();
        rig.Controller.Motion.BeginTeleport(() => { });
        rig.Controller.Motion.Update();

        rig.Session.Start(ChatRunKind.Send);
        rig.Advance(TimeSpan.FromMilliseconds(90));
        rig.Controller.Motion.Update();

        Assert.Equal(0, rig.Controller.Motion.OffsetY);
    }

    [Fact]
    public void Done_OnToolbar_Jumps()
    {
        using var rig = new Rig();
        rig.Session.Start(ChatRunKind.Send);
        rig.Advance(TimeSpan.FromMilliseconds(400));
        rig.Controller.Motion.Update();
        Assert.False(rig.Controller.Motion.IsAnimating);

        rig.Session.Emit(Ev.Done());

        Assert.True(rig.Controller.Motion.IsAnimating);
    }

    // ---- 探頭 ----

    [Fact]
    public void IdleOnToolbar_SinksAfterTenSeconds()
    {
        using var rig = new Rig();
        rig.Controller.Motion.SetPeekDepth(40);

        rig.Advance(TimeSpan.FromSeconds(9));
        rig.Controller.Tick();
        rig.Controller.Motion.Update();
        Assert.Equal(0, rig.Controller.Motion.OffsetY);

        rig.Advance(TimeSpan.FromSeconds(1));
        rig.Controller.Tick();
        rig.Advance(TimeSpan.FromMilliseconds(150));
        rig.Controller.Motion.Update();

        Assert.Equal(20, rig.Controller.Motion.OffsetY);
    }

    [Fact]
    public void SlotChange_StaysFullBodyForTenSeconds()
    {
        using var rig = new Rig(WorkbenchSpriteSlot.Welcome);
        rig.Advance(TimeSpan.FromSeconds(20));

        rig.Controller.Slot = WorkbenchSpriteSlot.Toolbar;
        rig.Controller.Motion.SetPeekDepth(40);
        rig.Controller.Tick();
        rig.Advance(TimeSpan.FromSeconds(9));
        rig.Controller.Tick();
        rig.Controller.Motion.Update();

        Assert.Equal(0, rig.Controller.Motion.OffsetY);
    }

    [Fact]
    public void NotIdle_RisesOnTick()
    {
        using var rig = new Rig();
        rig.Controller.Motion.SetPeekDepth(40);
        rig.Controller.Motion.Sink();
        rig.Advance(TimeSpan.FromMilliseconds(300));
        rig.Controller.Motion.Update();
        rig.Session.Start(ChatRunKind.Retry);

        rig.Controller.Tick();
        rig.Advance(TimeSpan.FromMilliseconds(100));
        rig.Controller.Motion.Update();

        Assert.Equal(20, rig.Controller.Motion.OffsetY);
    }

    [Fact]
    public void NearOnToolbar_Rises()
    {
        using var rig = new Rig();
        rig.Controller.Motion.SetPeekDepth(40);
        rig.Controller.Motion.Sink();
        rig.Advance(TimeSpan.FromMilliseconds(300));
        rig.Controller.Motion.Update();

        rig.Controller.SetNear(true);
        rig.Advance(TimeSpan.FromMilliseconds(100));
        rig.Controller.Motion.Update();

        Assert.Equal(20, rig.Controller.Motion.OffsetY);
    }

    [Fact]
    public void SlotNotToolbar_ClearsThePeekDepth()
    {
        using var rig = new Rig();
        rig.Controller.Motion.SetPeekDepth(40);
        rig.Controller.Motion.Sink();
        rig.Advance(TimeSpan.FromMilliseconds(300));
        rig.Controller.Motion.Update();

        rig.Controller.Slot = WorkbenchSpriteSlot.Welcome;

        Assert.Equal(0, rig.Controller.Motion.OffsetY);
    }

    // ---- 狀態變化事件 ----

    [Fact]
    public void StateChanged_FiresOnlyWhenTheStateChanges()
    {
        using var rig = new Rig();
        rig.Controller.Tick();
        rig.Controller.Tick();
        Assert.Empty(rig.States);

        rig.Session.Start();
        rig.Session.Emit(new TextDelta("a"));
        rig.Session.Emit(new TextDelta("b"));

        Assert.Equal([SpriteState.Thinking, SpriteState.Streaming], rig.States);
    }
}
