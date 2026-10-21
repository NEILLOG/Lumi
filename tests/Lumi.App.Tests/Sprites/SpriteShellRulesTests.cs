using Avalonia;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;

namespace Lumi.App.Tests.Sprites;

/// <summary>版面、位置夾回螢幕、自動休眠：純邏輯，不開視窗。</summary>
public class SpriteShellRulesTests
{
    private static readonly SpriteShellOptions Shell = new();
    private static readonly SpriteManifest OrbManifest = new OrbSpriteFactory().Manifest;

    private static SpriteLayout LayoutFor(double tier)
    {
        var options = SpriteLimits.Fit(OrbManifest, tier).Options;
        var anchor = new Point(options.Outer.Width / 2, options.BoxRect.Y + 0.08 * options.Box.Height);
        return SpriteLayout.Compute(options, anchor, Shell);
    }

    // ---- 版面 ----

    [Theory]
    [InlineData(64)]
    [InlineData(80)]
    [InlineData(104)]
    public void TheWindow_FitsTheBubbleTheSpriteAndTheInputBar(double tier)
    {
        var layout = LayoutFor(tier);
        var outer = SpriteLimits.Fit(OrbManifest, tier).Options.Outer;

        Assert.True(layout.WindowSize.Width >= Shell.MinWindowWidth);
        Assert.True(layout.WindowSize.Width >= outer.Width);
        Assert.Equal(Shell.BubbleReserve, layout.SpriteOrigin.Y);
        Assert.True(layout.InputBar.Top >= layout.SpriteBox.Bottom);                  // 輸入列在角色框下面
        Assert.True(layout.InputBar.Bottom + Shell.BottomMargin <= layout.WindowSize.Height + 0.001);
        Assert.True(layout.InputBar.Right <= layout.WindowSize.Width);
        Assert.True(layout.BubbleBottom > 0 && layout.BubbleBottom < layout.SpriteCenter.Y);   // 氣泡尖端在角色上半部
    }

    [Fact]
    public void TheSpriteIsHorizontallyCentered_AndTheCenterMatchesTheOuterRect()
    {
        var layout = LayoutFor(80);

        Assert.Equal(layout.WindowSize.Width / 2, layout.SpriteCenter.X, 6);
        Assert.Equal(layout.SpriteBox.Center.X, layout.SpriteCenter.X, 6);
        Assert.Equal(layout.SpriteBox.Center.Y, layout.SpriteCenter.Y, 6);
    }

    [Fact]
    public void ChangingTheTier_KeepsTheSpriteCenterOnScreen_SoItDoesNotJump()
    {
        var center = new PixelPoint(1500, 900);
        const double scaling = 2;
        var small = LayoutFor(64);
        var large = LayoutFor(104);

        var smallWindow = small.WindowPositionFor(center, scaling);
        var largeWindow = large.WindowPositionFor(center, scaling);

        Assert.Equal(center, small.SpriteCenterOnScreen(smallWindow, scaling));
        Assert.Equal(center, large.SpriteCenterOnScreen(largeWindow, scaling));
        Assert.NotEqual(smallWindow, largeWindow);   // 視窗本身要動，但角色中心不動
    }

    [Fact]
    public void WindowPosition_AndSpriteCenter_AreInverse_OnFractionalScaling()
    {
        var layout = LayoutFor(80);
        var window = new PixelPoint(-300, 120);   // 左邊的螢幕（負座標）

        var center = layout.SpriteCenterOnScreen(window, 1.25);

        Assert.Equal(window, layout.WindowPositionFor(center, 1.25));
    }

    // ---- 位置夾回螢幕 ----

    private static readonly ScreenArea Main = new(new PixelRect(0, 0, 1920, 1040), IsPrimary: true);
    private static readonly ScreenArea Right = new(new PixelRect(1920, 0, 1280, 1024), IsPrimary: false);

    [Fact]
    public void ACenterInsideTheWorkingArea_IsLeftAlone()
    {
        Assert.Equal(new PixelPoint(900, 500), SpritePlacementRules.Clamp(new PixelPoint(900, 500), [Main, Right], 48));
    }

    [Theory]
    [InlineData(10, 500, 48, 500)]       // 太靠左
    [InlineData(900, 1035, 900, 1040 - 48)]   // 太靠下
    [InlineData(1915, 5, 1920 - 48, 48)]  // 右上角
    public void ACenterNearTheEdge_IsPulledInsideThatScreen(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal(new PixelPoint(expectedX, expectedY), SpritePlacementRules.Clamp(new PixelPoint(x, y), [Main], 48));
    }

    [Fact]
    public void ACenterOnTheSecondScreen_IsClampedToTheSecondScreen()
    {
        var result = SpritePlacementRules.Clamp(new PixelPoint(3190, 1020), [Main, Right], 48);

        Assert.Equal(new PixelPoint(3200 - 48, 1024 - 48), result);
    }

    [Fact]
    public void ACenterOnAMissingScreen_FallsBackToThePrimaryBottomRight()
    {
        // 上次在右邊的外接螢幕，這次只剩主螢幕
        var result = SpritePlacementRules.Clamp(new PixelPoint(2500, 400), [Main], 48);

        Assert.Equal(SpritePlacementRules.DefaultCenter(Main.WorkingArea, 48), result);
        Assert.True(Main.WorkingArea.Contains(result));
        Assert.True(result.X > 1920 - 200 && result.Y > 1040 - 200);
    }

    [Fact]
    public void WithoutFallback_AnOffscreenCenterIsReturnedAsIs()
    {
        var offscreen = new PixelPoint(-5000, -5000);

        Assert.Equal(offscreen, SpritePlacementRules.Clamp(offscreen, [Main], 48, allowFallback: false));
    }

    [Fact]
    public void NoScreens_ReturnsTheInput()
    {
        Assert.Equal(new PixelPoint(1, 2), SpritePlacementRules.Clamp(new PixelPoint(1, 2), [], 48));
    }

    [Fact]
    public void ThePrimaryScreen_IsPickedEvenWhenItIsNotFirstInTheList()
    {
        var result = SpritePlacementRules.Clamp(new PixelPoint(-9000, 0), [Right, Main], 48);

        Assert.True(Main.WorkingArea.Contains(result));
    }

    // ---- 自動休眠 ----

    [Fact]
    public void IdleAndNobodyNear_SleepsAfterTheAutoSleepTime()
    {
        var time = new FakeTimeProvider();
        var policy = new SpriteSleepPolicy(Shell, time);

        Assert.Equal(SpriteState.Idle, policy.Update(SpriteState.Idle, near: false));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(SpriteState.Idle, policy.Update(SpriteState.Idle, near: false));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SpriteState.Sleep, policy.Update(SpriteState.Idle, near: false));
        Assert.True(policy.IsSleeping);
    }

    [Fact]
    public void ACursorComingNear_WakesTheSprite_AndRestartsTheClock()
    {
        var time = new FakeTimeProvider();
        var policy = new SpriteSleepPolicy(Shell, time);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(SpriteState.Sleep, policy.Update(SpriteState.Idle, near: false));

        Assert.Equal(SpriteState.Idle, policy.Update(SpriteState.Idle, near: true));
        Assert.False(policy.IsSleeping);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(SpriteState.Idle, policy.Update(SpriteState.Idle, near: false));   // 重新計時，沒有立刻又睡
    }

    [Theory]
    [InlineData(SpriteState.Thinking)]
    [InlineData(SpriteState.Tool)]
    [InlineData(SpriteState.Streaming)]
    [InlineData(SpriteState.Attention)]
    [InlineData(SpriteState.Error)]
    [InlineData(SpriteState.Done)]
    public void AnyActiveState_NeverSleeps_AndKeepsTheClockFresh(SpriteState active)
    {
        var time = new FakeTimeProvider();
        var policy = new SpriteSleepPolicy(Shell, time);
        time.Advance(TimeSpan.FromSeconds(500));

        Assert.Equal(active, policy.Update(active, near: false));
        Assert.Equal(SpriteState.Idle, policy.Update(SpriteState.Idle, near: false));   // 剛結束一件事：從現在重新算 60 秒
    }

    [Fact]
    public void Touch_RestartsTheClock_AndWakes()
    {
        var time = new FakeTimeProvider();
        var policy = new SpriteSleepPolicy(Shell, time);
        time.Advance(TimeSpan.FromSeconds(61));
        policy.Update(SpriteState.Idle, near: false);

        policy.Touch();

        Assert.False(policy.IsSleeping);
        Assert.Equal(SpriteState.Idle, policy.Update(SpriteState.Idle, near: false));
    }

    [Fact]
    public void ExplicitSleepRequest_IsHonored()
    {
        var policy = new SpriteSleepPolicy(Shell, new FakeTimeProvider());

        Assert.Equal(SpriteState.Sleep, policy.Update(SpriteState.Sleep, near: false));
        Assert.Equal(SpriteState.Idle, policy.Update(SpriteState.Idle, near: true));
    }
}
