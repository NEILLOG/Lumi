using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;

namespace Lumi.App.Tests.Sprites;

/// <summary>工作台角色的程式動作（plan/27 第 8.3 節）：時間軸逐點測，時間全部用假時鐘。</summary>
public class WorkbenchSpriteMotionTests
{
    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new();
        public WorkbenchSpriteMotion Motion { get; }

        public Rig() => Motion = new WorkbenchSpriteMotion(Time);

        /// <summary>推進假時鐘，再更新（畫面每幀做的事）。</summary>
        public void Step(double ms)
        {
            Time.Advance(TimeSpan.FromMilliseconds(ms));
            Motion.Update();
        }
    }

    private const double Tolerance = 0.01;

    private static void AssertNear(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

    // ---- 跳一下 ----

    [Fact]
    public void Jump_OffsetIsZeroAt0_MinusSixAt90_MinusTenAt180_ZeroAt360()
    {
        var rig = new Rig();
        rig.Motion.Jump();
        rig.Motion.Update();
        Assert.Equal(0, rig.Motion.OffsetY);

        rig.Step(90);
        Assert.Equal(-6, rig.Motion.OffsetY);

        rig.Step(90);
        Assert.Equal(-10, rig.Motion.OffsetY);

        rig.Step(180);
        Assert.Equal(0, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);
    }

    [Fact]
    public void Jump_OffsetIsAnIntegerMidway()
    {
        var rig = new Rig();
        rig.Motion.Jump();

        rig.Step(10);   // −6 × 10 ÷ 90 ≈ −0.67，要四捨五入成 −1

        Assert.Equal(-1, rig.Motion.OffsetY);
        Assert.Equal(Math.Round(rig.Motion.OffsetY), rig.Motion.OffsetY);
    }

    [Fact]
    public void Jump_WhileJumping_DoesNotRestart()
    {
        var rig = new Rig();
        rig.Motion.Jump();
        rig.Step(90);

        rig.Motion.Jump();   // 不重來：仍然是 180 ms 的 −10，不是重新從 0 開始
        rig.Step(90);

        Assert.Equal(-10, rig.Motion.OffsetY);
    }

    [Fact]
    public void Jump_WhileTeleporting_IsIgnored()
    {
        var rig = new Rig();
        rig.Motion.BeginTeleport(() => { });
        rig.Motion.Update();

        rig.Motion.Jump();
        rig.Step(90);

        Assert.Equal(0, rig.Motion.OffsetY);
    }

    [Fact]
    public void Jump_WhenSunk_RisesFirst_ThenJumpsOnce()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);
        Assert.Equal(40, rig.Motion.OffsetY);

        rig.Motion.Jump();
        rig.Step(100);
        Assert.Equal(20, rig.Motion.OffsetY);   // 還在冒出，還沒跳

        rig.Step(100);
        Assert.Equal(0, rig.Motion.OffsetY);    // 冒完，這一刻才開始跳

        rig.Step(90);
        Assert.Equal(-6, rig.Motion.OffsetY);

        rig.Step(270);
        Assert.Equal(0, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);   // 只跳一次，沒有排隊
    }

    [Fact]
    public void Jump_WhileRising_WaitsForTheRiseToFinish()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);

        rig.Motion.Rise();
        rig.Step(50);
        Assert.Equal(30, rig.Motion.OffsetY);

        rig.Motion.Jump();
        rig.Step(50);
        Assert.Equal(20, rig.Motion.OffsetY);   // 冒出中的跳躍一樣等

        rig.Step(100);
        Assert.Equal(0, rig.Motion.OffsetY);

        rig.Step(90);
        Assert.Equal(-6, rig.Motion.OffsetY);
    }

    [Fact]
    public void Jump_WhileSunkHalfway_RisesThenJumps()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(150);
        Assert.Equal(20, rig.Motion.OffsetY);

        rig.Motion.Jump();
        rig.Step(100);

        Assert.Equal(0, rig.Motion.OffsetY);
        rig.Step(90);
        Assert.Equal(-6, rig.Motion.OffsetY);
    }

    // ---- 瞬移 ----

    [Fact]
    public void Teleport_OpacityAt0_60_120_210_300()
    {
        var rig = new Rig();
        rig.Motion.BeginTeleport(() => { });
        rig.Motion.Update();
        AssertNear(1, rig.Motion.Opacity);

        rig.Step(60);
        AssertNear(0.5, rig.Motion.Opacity);

        rig.Step(60);
        AssertNear(0, rig.Motion.Opacity);

        rig.Step(90);
        AssertNear(0.5, rig.Motion.Opacity);

        rig.Step(90);
        AssertNear(1, rig.Motion.Opacity);
        Assert.False(rig.Motion.IsTeleporting);
    }

    [Fact]
    public void Teleport_SwapIsCalledOnceAt120()
    {
        var rig = new Rig();
        var swaps = 0;
        rig.Motion.BeginTeleport(() => swaps++);

        rig.Step(60);
        Assert.Equal(0, swaps);

        rig.Step(60);
        Assert.Equal(1, swaps);

        rig.Step(240);
        Assert.Equal(1, swaps);
    }

    [Fact]
    public void Teleport_BeginWhileRunning_DoesNotRestart()
    {
        var rig = new Rig();
        var first = 0;
        var second = 0;
        rig.Motion.BeginTeleport(() => first++);
        var startedAt = rig.Motion.LastTeleportAt;
        rig.Step(60);

        rig.Motion.BeginTeleport(() => second++);
        rig.Step(60);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(startedAt, rig.Motion.LastTeleportAt);
    }

    [Fact]
    public void LastTeleportAt_IsNullUntilATeleport()
    {
        var rig = new Rig();
        Assert.Null(rig.Motion.LastTeleportAt);

        rig.Motion.BeginTeleport(() => { });

        Assert.Equal(rig.Time.GetUtcNow(), rig.Motion.LastTeleportAt);
    }

    // ---- 探頭 ----

    [Fact]
    public void Rise_From40_Is20At100ms_AndZeroAt200ms()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);

        rig.Motion.Rise();
        rig.Step(100);
        Assert.Equal(20, rig.Motion.OffsetY);

        rig.Step(100);
        Assert.Equal(0, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);
    }

    [Fact]
    public void Sink_ReachesTheBottomAt300ms()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);

        rig.Motion.Sink();
        rig.Step(150);
        Assert.Equal(20, rig.Motion.OffsetY);

        rig.Step(150);
        Assert.Equal(40, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);
    }

    [Fact]
    public void Sink_ThenRise_ReturnsFromTheCurrentPosition()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(150);

        rig.Motion.Rise();
        rig.Step(50);

        Assert.Equal(10, rig.Motion.OffsetY);
    }

    [Fact]
    public void Sink_AfterIdleWithoutFrames_StartsFromTheTopNotAJumpAhead()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Time.Advance(TimeSpan.FromSeconds(5));   // 沒有畫面更新的空檔

        rig.Motion.Sink();
        rig.Step(150);

        Assert.Equal(20, rig.Motion.OffsetY);
    }

    [Fact]
    public void SetPeekDepthZero_IsFullBodyImmediately()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);

        rig.Motion.SetPeekDepth(0);

        Assert.Equal(0, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);
    }

    [Fact]
    public void SetPeekDepth_Negative_IsZero()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(-5);
        rig.Motion.Sink();
        rig.Step(300);

        Assert.Equal(0, rig.Motion.OffsetY);
    }

    [Fact]
    public void SetPeekDepth_FromFullBody_DoesNotSinkByItself()
    {
        var rig = new Rig();

        rig.Motion.SetPeekDepth(40);
        rig.Step(300);

        Assert.Equal(0, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);
    }

    [Fact]
    public void SetPeekDepth_WhenSunkToTheBottom_SinksToTheNewDepth()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);

        rig.Motion.SetPeekDepth(60);
        rig.Step(50);
        Assert.Equal(50, rig.Motion.OffsetY);

        rig.Step(50);
        Assert.Equal(60, rig.Motion.OffsetY);
    }

    [Fact]
    public void SetPeekDepth_Shrinking_ClampsTheCurrentDepth()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);

        rig.Motion.SetPeekDepth(20);

        Assert.Equal(20, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);
    }

    // ---- 狀態與重設 ----

    [Fact]
    public void Rise_AtFullBody_HasNoEffect()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);

        rig.Motion.Rise();

        Assert.False(rig.Motion.IsAnimating);
        Assert.Equal(0, rig.Motion.OffsetY);
    }

    [Fact]
    public void Reset_CallsThePendingSwapAndReturnsToFullBody()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);
        var swaps = 0;
        rig.Motion.BeginTeleport(() => swaps++);
        rig.Step(60);

        rig.Motion.Reset();

        Assert.Equal(1, swaps);
        Assert.False(rig.Motion.IsTeleporting);
        AssertNear(1, rig.Motion.Opacity);
        Assert.Equal(0, rig.Motion.OffsetY);
        Assert.False(rig.Motion.IsAnimating);
    }

    [Fact]
    public void Reset_AfterTheSwapWasCalled_DoesNotCallItAgain()
    {
        var rig = new Rig();
        var swaps = 0;
        rig.Motion.BeginTeleport(() => swaps++);
        rig.Step(120);

        rig.Motion.Reset();

        Assert.Equal(1, swaps);
    }

    [Fact]
    public void Reset_KeepsTheDepthSetting()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        rig.Step(300);

        rig.Motion.Reset();
        rig.Motion.Sink();
        rig.Step(300);

        Assert.Equal(40, rig.Motion.OffsetY);
    }

    [Fact]
    public void IsAnimating_IsFalseOnceTheMotionEnds()
    {
        var rig = new Rig();
        rig.Motion.SetPeekDepth(40);
        rig.Motion.Sink();
        Assert.True(rig.Motion.IsAnimating);
        rig.Step(300);
        Assert.False(rig.Motion.IsAnimating);

        rig.Motion.SetPeekDepth(0);   // 回到全身，跳一下才會立刻開始
        rig.Motion.Jump();
        Assert.True(rig.Motion.IsAnimating);
        rig.Step(360);
        Assert.False(rig.Motion.IsAnimating);
    }
}
