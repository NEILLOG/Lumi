using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>plan/07 的「對話事件→角色狀態」表，每一列一個測試。</summary>
public class SpriteStateMapperTests
{
    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new();
        public SpriteStateMapper Mapper { get; }
        public List<SpriteState> Changes { get; } = [];

        public Rig()
        {
            Mapper = new SpriteStateMapper(new SpriteShellOptions(), Time);
            Mapper.Changed += Changes.Add;
        }

        public void Feed(params ChatEvent[] events)
        {
            foreach (var e in events)
                Mapper.OnEvent(e);
        }
    }

    [Fact]
    public void RunStarted_IsThinking()
    {
        var rig = new Rig();

        rig.Mapper.OnRunStarted();

        Assert.Equal(SpriteState.Thinking, rig.Mapper.State);
    }

    [Fact]
    public void ThinkingDelta_IsThinking_TextDelta_IsStreaming()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();

        rig.Feed(new TextDelta("嗨"));
        Assert.Equal(SpriteState.Streaming, rig.Mapper.State);

        rig.Feed(new ThinkingDelta("想"));
        Assert.Equal(SpriteState.Thinking, rig.Mapper.State);
    }

    [Fact]
    public void ToolCallStarted_IsTool_AndCompletedGoesBackToThinking()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();

        rig.Feed(Ev.Tool());
        Assert.Equal(SpriteState.Tool, rig.Mapper.State);

        rig.Feed(Ev.ToolDone());
        Assert.Equal(SpriteState.Thinking, rig.Mapper.State);   // 等模型根據結果回應
    }

    [Fact]
    public void ApprovalRequired_IsAttention_UntilAnswered_ThenTool()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Feed(Ev.Tool("c1", permission: Lumi.Core.Tools.ToolPermission.Write), Ev.Approval("c1"));
        Assert.Equal(SpriteState.Attention, rig.Mapper.State);

        rig.Feed(new ThinkingDelta("…"));   // 等待確認時不會被別的事件蓋掉
        Assert.Equal(SpriteState.Attention, rig.Mapper.State);

        rig.Mapper.OnApprovalResolved("other");   // 別的呼叫的回答與這張無關
        Assert.Equal(SpriteState.Attention, rig.Mapper.State);

        rig.Mapper.OnApprovalResolved("c1");
        Assert.Equal(SpriteState.Tool, rig.Mapper.State);
    }

    [Fact]
    public void CompletedWithToolUse_DoesNotEndTheRun()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Feed(new TextDelta("我來查。"), Ev.ToolTurnDone());

        Assert.Equal(SpriteState.Streaming, rig.Mapper.State);   // 不變
    }

    [Fact]
    public void ALoopWithSeveralCompleted_OnlyTheLastOneIsDone()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Feed(Ev.Tool(), Ev.ToolTurnDone(), Ev.ToolDone(), new TextDelta("結果"), Ev.Done("結果"));

        Assert.Equal(SpriteState.Done, rig.Mapper.State);
        Assert.DoesNotContain(SpriteState.Done, rig.Changes.SkipLast(1));
    }

    [Fact]
    public void Done_ReturnsToIdleAfterThreeSeconds()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Feed(Ev.Done());
        rig.Mapper.OnRunFinished(Ev.Finished());

        rig.Time.Advance(TimeSpan.FromSeconds(2.9));
        rig.Mapper.Tick();
        Assert.Equal(SpriteState.Done, rig.Mapper.State);

        rig.Time.Advance(TimeSpan.FromSeconds(0.2));
        rig.Mapper.Tick();
        Assert.Equal(SpriteState.Idle, rig.Mapper.State);
    }

    [Theory]
    [InlineData(StopReason.MaxTokens)]
    [InlineData(StopReason.StopSequence)]
    [InlineData(StopReason.Other)]
    public void AnyNonToolUseEnd_IsDone(StopReason reason)
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();

        rig.Feed(Ev.Done("x", reason));

        Assert.Equal(SpriteState.Done, rig.Mapper.State);
    }

    [Fact]
    public void FailureEvent_IsError_AndItStaysUntilAcknowledged()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Feed(new Failed(Ev.Failure()));
        rig.Mapper.OnRunFinished(Ev.Finished(Ev.Failure()));

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        rig.Mapper.Tick();
        Assert.Equal(SpriteState.Error, rig.Mapper.State);

        rig.Mapper.Acknowledge();
        Assert.Equal(SpriteState.Idle, rig.Mapper.State);
    }

    [Fact]
    public void AnUnexpectedError_IsError()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();

        rig.Mapper.OnRunFinished(Ev.Finished(unexpected: true));

        Assert.Equal(SpriteState.Error, rig.Mapper.State);
    }

    [Fact]
    public void TheUserStopping_ReturnsToIdle_EvenWhileWaitingForApproval()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Feed(Ev.Tool(), Ev.Approval());

        rig.Mapper.OnRunFinished(Ev.Finished(cancelled: true));

        Assert.Equal(SpriteState.Idle, rig.Mapper.State);
    }

    [Fact]
    public void AStreamThatEndsWithoutCompleting_ReturnsToIdle()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Feed(new TextDelta("半句"));

        rig.Mapper.OnRunFinished(Ev.Finished());

        Assert.Equal(SpriteState.Idle, rig.Mapper.State);
    }

    [Fact]
    public void ANewRun_LeavesErrorImmediately()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();
        rig.Mapper.OnRunFinished(Ev.Finished(Ev.Failure()));

        rig.Mapper.OnRunStarted();

        Assert.Equal(SpriteState.Thinking, rig.Mapper.State);
    }

    [Fact]
    public void ChangedIsOnlyRaisedWhenTheStateReallyChanges()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();

        rig.Feed(new TextDelta("a"), new TextDelta("b"), new TextDelta("c"));

        Assert.Equal([SpriteState.Thinking, SpriteState.Streaming], rig.Changes);
    }

    [Fact]
    public void Acknowledge_DoesNothingOutsideError()
    {
        var rig = new Rig();
        rig.Mapper.OnRunStarted();

        rig.Mapper.Acknowledge();

        Assert.Equal(SpriteState.Thinking, rig.Mapper.State);
    }
}
