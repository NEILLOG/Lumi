using Avalonia;
using Lumi.App.Views;
using Lumi.Core.Models;

namespace Lumi.App.Tests.Views;

/// <summary>M9 9D：工作台視窗位置還原的規則（純函式，不開視窗）。單位假設見 <see cref="WindowPlacementRules"/>。</summary>
public class WindowPlacementRulesTests
{
    private const double MinW = 500;
    private const double MinH = 420;

    private static readonly WindowScreen Main = new(new PixelRect(0, 0, 1920, 1080), 1.0);

    private static WindowSettings At(int x, int y, double w = 1040, double h = 760, double scaling = 1.0, bool maximized = false) =>
        new(x, y, w, h, maximized, scaling);

    private static WindowPlacement? Restore(WindowSettings? saved, params WindowScreen[] screens) =>
        WindowPlacementRules.Restore(saved, screens, MinW, MinH);

    // ---- 單螢幕 ----

    [Fact]
    public void NoSavedValue_UsesTheDefault()
    {
        Assert.Null(Restore(null, Main));
    }

    [Fact]
    public void SingleScreen_InsideTheWorkArea_RestoresPositionAndSize()
    {
        var placement = Restore(At(200, 120, 1100, 800, maximized: false), Main);

        Assert.NotNull(placement);
        Assert.Equal(new PixelPoint(200, 120), placement!.Position);
        Assert.Equal(1100, placement.Width);
        Assert.Equal(800, placement.Height);
        Assert.False(placement.Maximized);
    }

    [Fact]
    public void Maximized_IsCarriedThrough_WithTheNormalBounds()
    {
        var placement = Restore(At(200, 120, 1100, 800, maximized: true), Main);

        Assert.NotNull(placement);
        Assert.True(placement!.Maximized);
        Assert.Equal(1100, placement.Width);
        Assert.Equal(800, placement.Height);
    }

    // ---- 多螢幕（含負座標） ----

    [Fact]
    public void SecondScreenToTheLeft_WithNegativeOrigin_IsAccepted()
    {
        var left = new WindowScreen(new PixelRect(-1920, 0, 1920, 1080), 1.0);

        var placement = Restore(At(-1500, 100), Main, left);

        Assert.NotNull(placement);
        Assert.Equal(new PixelPoint(-1500, 100), placement!.Position);
    }

    [Fact]
    public void SecondScreenAbove_WithNegativeY_IsAccepted()
    {
        var above = new WindowScreen(new PixelRect(0, -1080, 1920, 1080), 1.0);

        Assert.NotNull(Restore(At(300, -900), Main, above));
    }

    [Fact]
    public void WindowLargerThanOneScreen_ButInsideTheUnion_IsAccepted()
    {
        // 寬 2500 超過單一螢幕，但兩個螢幕的聯集有 3840；標題列在左邊那一塊
        var right = new WindowScreen(new PixelRect(1920, 0, 1920, 1080), 1.0);

        Assert.NotNull(Restore(At(100, 100, 2500, 800), Main, right));
    }

    [Fact]
    public void TitleBarStraddlingTwoScreens_IsRejected()
    {
        var right = new WindowScreen(new PixelRect(1920, 0, 1920, 1080), 1.0);

        // 標題列區域 100 px 寬，從 1900 到 2000 跨過兩個螢幕的交界
        Assert.Null(Restore(At(1900, 100), Main, right));
    }

    // ---- 螢幕被拔掉 ----

    [Fact]
    public void ScreenWasUnplugged_WindowIsOnTheMissingScreen_UsesTheDefault()
    {
        // 上次在右邊的外接螢幕（x 3000），現在只剩主螢幕
        Assert.Null(Restore(At(3000, 200), Main));
    }

    [Fact]
    public void NoScreens_UsesTheDefault()
    {
        Assert.Null(WindowPlacementRules.Restore(At(0, 0), [], MinW, MinH));
    }

    // ---- 縮放改變 ----

    [Fact]
    public void ScalingChanged_SizeFitsAtTheOldScale_ButNotAtTheNewOne_UsesTheDefault()
    {
        // 上次在 1× 螢幕存的 1040 DIP 寬。現在這個螢幕是 2×、工作區實體像素 1920：1040 DIP = 2080 px 放不下
        var hiDpi = new WindowScreen(new PixelRect(0, 0, 1920, 1080), 2.0);

        Assert.Null(Restore(At(0, 0, 1040, 760, scaling: 1.0), hiDpi));
    }

    [Fact]
    public void ScalingChanged_SameDipSize_FitsWhenTheScreenIsLargeEnough()
    {
        // 同樣的 DIP 寬，但工作區實體像素夠大（3840 px）：2080 px 放得下，DIP 不變（邏輯大小不因縮放改變）
        var hiDpi = new WindowScreen(new PixelRect(0, 0, 3840, 2160), 2.0);

        var placement = Restore(At(0, 0, 1040, 760, scaling: 1.0), hiDpi);

        Assert.NotNull(placement);
        Assert.Equal(1040, placement!.Width);
        Assert.Equal(760, placement.Height);
    }

    [Fact]
    public void TitleBarProbe_IsScaledToTheTargetScreen()
    {
        // 標題列檢查區域是 40 DIP：150% 螢幕上是 60 px，1× 螢幕上是 40 px。視窗頂端在 y=1030：
        // 1030 + 60 = 1090 > 1080 → 150% 螢幕上不通過；1030 + 40 = 1070 ≤ 1080 → 1× 螢幕上通過。
        var screen150 = new WindowScreen(new PixelRect(0, 0, 3840, 1080), 1.5);
        var screen100 = new WindowScreen(new PixelRect(0, 0, 3840, 1080), 1.0);

        Assert.Null(Restore(At(0, 1030, 600, 500, scaling: 1.5), screen150));
        Assert.NotNull(Restore(At(0, 1030, 600, 500, scaling: 1.0), screen100));
    }

    // ---- 最小與最大尺寸 ----

    [Fact]
    public void BelowMinimumWidth_UsesTheDefault()
    {
        Assert.Null(Restore(At(100, 100, 499, 760), Main));
    }

    [Fact]
    public void BelowMinimumHeight_UsesTheDefault()
    {
        Assert.Null(Restore(At(100, 100, 1040, 419), Main));
    }

    [Fact]
    public void ExactlyTheMinimum_IsAccepted()
    {
        Assert.NotNull(Restore(At(100, 100, MinW, MinH), Main));
    }

    [Fact]
    public void LargerThanAllScreens_UsesTheDefault()
    {
        Assert.Null(Restore(At(0, 0, 2500, 760), Main));
        Assert.Null(Restore(At(0, 0, 1040, 1500), Main));
    }

    [Fact]
    public void ReallyHugeValues_AreCappedAndThenRejected()
    {
        Assert.Null(Restore(At(0, 0, 1e12, 1e12), Main));
    }

    // ---- 標題列在螢幕外 ----

    [Fact]
    public void TitleBarAboveTheTopEdge_UsesTheDefault()
    {
        Assert.Null(Restore(At(200, -10), Main));
    }

    [Fact]
    public void TitleBarBelowTheBottomEdge_UsesTheDefault()
    {
        Assert.Null(Restore(At(200, 1050), Main));
    }

    [Fact]
    public void TitleBarRightOfTheScreen_UsesTheDefault()
    {
        Assert.Null(Restore(At(1910, 100), Main));
    }

    [Fact]
    public void WindowHangingOffTheRightEdge_WithTitleBarVisible_IsAccepted()
    {
        // 標題列還在螢幕裡（右半露出的部分不影響判斷）：只要標題列完整可見就能拖回來
        Assert.NotNull(Restore(At(1500, 100, 1040, 760), Main));
    }

    // ---- 不合法的值 ----

    [Fact]
    public void NaNSize_UsesTheDefault()
    {
        Assert.Null(Restore(At(100, 100, double.NaN, 760), Main));
        Assert.Null(Restore(At(100, 100, 1040, double.NaN), Main));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void BadScaling_UsesTheDefault(double scaling)
    {
        Assert.Null(Restore(At(100, 100, 1040, 760, scaling), Main));
    }
}
