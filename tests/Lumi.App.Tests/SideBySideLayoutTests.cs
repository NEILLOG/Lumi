using Avalonia;
using Lumi.App.Views;

namespace Lumi.App.Tests;

/// <summary>並排的版面計算（M35 35G）：左右各半、奇數寬、第二個螢幕的位移、太窄不能並排。</summary>
public class SideBySideLayoutTests
{
    [Fact]
    public void FullHd_AtScale1_SplitsEvenlyWithFullHeight()
    {
        var layout = SideBySideLayout.Compute(new PixelRect(0, 0, 1920, 1080), 1);

        Assert.NotNull(layout);
        Assert.Equal(new PixelRect(0, 0, 960, 1080), layout.Value.Left);
        Assert.Equal(new PixelRect(960, 0, 960, 1080), layout.Value.Right);
    }

    [Fact]
    public void FullHd_AtScale1Point5_StillSplitsBecauseDipWidthIsEnough()
    {
        // 1920 ÷ 1.5 = 1280 DIP，夠寬
        var layout = SideBySideLayout.Compute(new PixelRect(0, 0, 1920, 1080), 1.5);

        Assert.NotNull(layout);
        Assert.Equal(new PixelRect(0, 0, 960, 1080), layout.Value.Left);
        Assert.Equal(new PixelRect(960, 0, 960, 1080), layout.Value.Right);
    }

    [Fact]
    public void OddWidth_LeftGetsTheExtraPixel()
    {
        var layout = SideBySideLayout.Compute(new PixelRect(0, 0, 1921, 1000), 1);

        Assert.NotNull(layout);
        Assert.Equal(961, layout.Value.Left.Width);
        Assert.Equal(960, layout.Value.Right.Width);
        Assert.Equal(layout.Value.Left.Right, layout.Value.Right.X);
        Assert.Equal(1921, layout.Value.Left.Width + layout.Value.Right.Width);
    }

    [Fact]
    public void WorkingAreaOffset_SecondScreen_IsKeptOnBothHalves()
    {
        var layout = SideBySideLayout.Compute(new PixelRect(1920, 40, 1920, 1040), 1);

        Assert.NotNull(layout);
        Assert.Equal(new PixelRect(1920, 40, 960, 1040), layout.Value.Left);
        Assert.Equal(new PixelRect(2880, 40, 960, 1040), layout.Value.Right);
    }

    [Fact]
    public void TooNarrowInDip_ReturnsNull()
    {
        // 1920 ÷ 2 = 960 DIP，不到 1000
        Assert.Null(SideBySideLayout.Compute(new PixelRect(0, 0, 1920, 1080), 2));
        Assert.Null(SideBySideLayout.Compute(new PixelRect(0, 0, 999, 800), 1));
    }

    [Fact]
    public void ExactlyTheMinimum_IsAllowed()
    {
        Assert.NotNull(SideBySideLayout.Compute(new PixelRect(0, 0, 1000, 800), 1));
    }

    [Fact]
    public void InvalidScaling_ReturnsNull()
    {
        Assert.Null(SideBySideLayout.Compute(new PixelRect(0, 0, 1920, 1080), 0));
        Assert.Null(SideBySideLayout.Compute(new PixelRect(0, 0, 1920, 1080), double.NaN));
    }
}
