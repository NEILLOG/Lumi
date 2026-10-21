using Avalonia;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;

namespace Lumi.App.Tests.Sprites;

/// <summary>M0 在 Mac／Windows 手動驗證過的規則（POC 清單 5a、靠近與離開延遲），現在有測試保護。</summary>
public class SpriteInteractionTrackerTests
{
    private const double Radius = 150;

    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new();
        public SpriteShellOptions Options { get; } = new();
        public SpriteInteractionTracker Tracker { get; }
        public List<TrackerEvent> Events { get; } = [];

        public Rig()
        {
            Tracker = new SpriteInteractionTracker(Options, Time);
            Tracker.Raised += Events.Add;
        }

        public void Wait(double seconds) => Time.Advance(TimeSpan.FromSeconds(seconds));

        public void PollFar(bool keepNear = false) => Tracker.Poll(new Vector(500, 0), Radius, keepNear);

        public void PollNear() => Tracker.Poll(new Vector(20, 10), Radius, false);

        public IEnumerable<T> Of<T>() where T : TrackerEvent => Events.OfType<T>();
    }

    // ---- 靠近 ----

    [Fact]
    public void CursorInsideTheRadius_IsNear_AndRaisesOnce()
    {
        var rig = new Rig();

        rig.PollNear();
        rig.Wait(0.03);
        rig.PollNear();

        Assert.True(rig.Tracker.IsNear);
        Assert.Equal([new TrackerEvent.NearChanged(true)], rig.Of<TrackerEvent.NearChanged>());
    }

    [Fact]
    public void LeavingTheRadius_OnlyCountsAfterTheHoldTime()
    {
        var rig = new Rig();
        rig.PollNear();

        rig.Wait(0.3);
        rig.PollFar();
        Assert.True(rig.Tracker.IsNear);          // 0.3 秒 < 0.6 秒：還沒離開

        rig.Wait(0.4);
        rig.PollFar();
        Assert.False(rig.Tracker.IsNear);         // 距離上次靠近 0.7 秒
        Assert.Equal([true, false], rig.Of<TrackerEvent.NearChanged>().Select(e => e.IsNear));
    }

    [Fact]
    public void ComingBackWithinTheHold_NeverRaisesLeave()
    {
        var rig = new Rig();
        rig.PollNear();
        rig.Wait(0.4);
        rig.PollFar();
        rig.Wait(0.1);
        rig.PollNear();
        rig.Wait(0.4);
        rig.PollFar();

        Assert.True(rig.Tracker.IsNear);
        Assert.Single(rig.Of<TrackerEvent.NearChanged>());
    }

    [Fact]
    public void KeepNear_HoldsNearEvenWhenTheCursorIsFar()
    {
        var rig = new Rig();   // 游標在輸入列上、或正在打字
        rig.PollNear();

        for (var i = 0; i < 100; i++)
        {
            rig.Wait(0.1);
            rig.PollFar(keepNear: true);
        }

        Assert.True(rig.Tracker.IsNear);
    }

    [Fact]
    public void WithoutNativeCursor_TheFallbackUsesWhetherThePointerIsInsideTheWindow()
    {
        var rig = new Rig();

        rig.Tracker.Poll(null, Radius, keepNear: false, insideFallback: true);
        Assert.True(rig.Tracker.IsNear);

        rig.Wait(1);
        rig.Tracker.Poll(null, Radius, keepNear: false, insideFallback: false);
        Assert.False(rig.Tracker.IsNear);
        Assert.Empty(rig.Of<TrackerEvent.Looking>());   // 沒有游標座標就沒有「看向」
    }

    [Fact]
    public void Look_IsNormalizedByTheRadius_AndClamped()
    {
        var rig = new Rig();

        rig.Tracker.Poll(new Vector(75, -300), Radius, false);

        var look = Assert.Single(rig.Of<TrackerEvent.Looking>());
        Assert.Equal(0.5, look.Direction.X, 6);
        Assert.Equal(-1, look.Direction.Y, 6);
    }

    [Fact]
    public void Look_IsOnlyRaisedWhenItChanges()
    {
        var rig = new Rig();

        rig.Tracker.Poll(new Vector(30, 0), Radius, false);
        rig.Tracker.Poll(new Vector(30, 0), Radius, false);
        rig.Tracker.Poll(new Vector(30.1, 0), Radius, false);   // 變化小於門檻
        rig.Tracker.Poll(new Vector(60, 0), Radius, false);

        Assert.Equal(2, rig.Of<TrackerEvent.Looking>().Count());
    }

    [Fact]
    public void WhilePressed_NearIsKept()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));

        for (var i = 0; i < 40; i++)
        {
            rig.Wait(0.1);
            rig.PollFar();
        }

        Assert.True(rig.Tracker.IsNear);
    }

    // ---- 戳 ----

    [Fact]
    public void HoldingStill_BecomesAPokeAfterTheLongPress_ThenEndsOnRelease()
    {
        var rig = new Rig();
        var at = new Point(40, 30);
        rig.Tracker.PointerPressed(at);

        rig.Wait(0.3);
        rig.PollNear();
        Assert.False(rig.Tracker.IsPoking);        // 還沒到 0.45 秒

        rig.Wait(0.2);
        rig.PollNear();
        Assert.True(rig.Tracker.IsPoking);
        Assert.Equal(at, Assert.Single(rig.Of<TrackerEvent.PokeBegan>()).At);

        rig.Tracker.PointerReleased();
        Assert.Single(rig.Of<TrackerEvent.PokeEnded>());
        Assert.Empty(rig.Of<TrackerEvent.Clicked>());
        Assert.Empty(rig.Of<TrackerEvent.DragBegan>());
    }

    [Fact]
    public void AfterPoking_ASmallWobble_DoesNotTurnIntoADrag()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));
        rig.Wait(0.5);
        rig.PollNear();

        rig.Tracker.PointerMoved(new Vector(2, 1));      // 手抖
        rig.Tracker.PointerMoved(new Vector(30, 30));    // 戳住之後，即使移得很遠也不變拖曳
        rig.Tracker.PointerReleased();

        Assert.True(rig.Of<TrackerEvent.PokeBegan>().Any());
        Assert.Empty(rig.Of<TrackerEvent.DragBegan>());
        Assert.Empty(rig.Of<TrackerEvent.DragMoved>());
    }

    [Fact]
    public void AMoveBelowTheThreshold_DuringAHold_StillPokes()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));
        rig.Tracker.PointerMoved(new Vector(3, -3));     // 小於 4 DIP
        rig.Wait(0.5);
        rig.PollNear();

        Assert.True(rig.Tracker.IsPoking);
        Assert.False(rig.Tracker.IsDragging);
    }

    // ---- 拖曳 ----

    [Fact]
    public void ShortPressThenMovingFast_IsADrag_AndNeverPokes()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));
        rig.Wait(0.1);

        rig.Tracker.PointerMoved(new Vector(5, 0));
        rig.Wait(0.6);                                   // 超過長按時間也不會戳
        rig.PollNear();
        rig.Tracker.PointerMoved(new Vector(40, 12));
        rig.Tracker.PointerReleased();

        Assert.Equal(
            [typeof(TrackerEvent.DragBegan), typeof(TrackerEvent.DragMoved), typeof(TrackerEvent.DragMoved), typeof(TrackerEvent.DragEnded)],
            rig.Events.Where(e => e is not (TrackerEvent.NearChanged or TrackerEvent.Looking)).Select(e => e.GetType()));
        Assert.Equal(new Vector(40, 12), rig.Of<TrackerEvent.DragMoved>().Last().Delta);
        Assert.Empty(rig.Of<TrackerEvent.PokeBegan>());
        Assert.Empty(rig.Of<TrackerEvent.Clicked>());
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(0, -4)]
    [InlineData(-6, 2)]
    public void ReachingTheThresholdOnEitherAxis_StartsTheDrag(double dx, double dy)
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));

        rig.Tracker.PointerMoved(new Vector(dx, dy));

        Assert.True(rig.Tracker.IsDragging);
    }

    [Fact]
    public void MovingWithoutPressing_DoesNothing()
    {
        var rig = new Rig();

        rig.Tracker.PointerMoved(new Vector(100, 100));
        rig.Tracker.PointerReleased();

        Assert.Empty(rig.Events);
    }

    // ---- 點擊與取消 ----

    [Fact]
    public void ShortPressAndRelease_IsAClick()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));
        rig.Wait(0.1);
        rig.PollNear();

        rig.Tracker.PointerReleased();

        Assert.Single(rig.Of<TrackerEvent.Clicked>());
        Assert.False(rig.Tracker.IsPressed);
    }

    [Fact]
    public void ReleasingTwice_RaisesOnlyOnce()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));

        rig.Tracker.PointerReleased();
        rig.Tracker.PointerReleased();

        Assert.Single(rig.Of<TrackerEvent.Clicked>());
    }

    [Fact]
    public void CaptureLostWhileDragging_EndsTheDrag_WithoutAClick()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));
        rig.Tracker.PointerMoved(new Vector(20, 0));

        rig.Tracker.CaptureLost();

        Assert.Single(rig.Of<TrackerEvent.DragEnded>());
        Assert.Empty(rig.Of<TrackerEvent.Clicked>());
        Assert.False(rig.Tracker.IsPressed);
    }

    [Fact]
    public void CaptureLostWhilePoking_EndsThePoke_WithoutAClick()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));
        rig.Wait(0.5);
        rig.PollNear();

        rig.Tracker.CaptureLost();

        Assert.Single(rig.Of<TrackerEvent.PokeEnded>());
        Assert.Empty(rig.Of<TrackerEvent.Clicked>());
    }

    [Fact]
    public void CaptureLostAfterAShortPress_IsSilent()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));

        rig.Tracker.CaptureLost();

        Assert.Empty(rig.Of<TrackerEvent.Clicked>());
        Assert.Empty(rig.Of<TrackerEvent.PokeEnded>());
        Assert.Empty(rig.Of<TrackerEvent.DragEnded>());
    }

    [Fact]
    public void ANewPress_AfterADrag_StartsFresh()
    {
        var rig = new Rig();
        rig.Tracker.PointerPressed(new Point(10, 10));
        rig.Tracker.PointerMoved(new Vector(30, 0));
        rig.Tracker.PointerReleased();
        rig.Events.Clear();

        rig.Tracker.PointerPressed(new Point(12, 12));
        rig.Tracker.PointerReleased();

        Assert.Equal([typeof(TrackerEvent.Clicked)], rig.Events.Select(e => e.GetType()));
    }
}
