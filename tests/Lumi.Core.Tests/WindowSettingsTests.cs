using Lumi.Core.Models;

namespace Lumi.Core.Tests;

/// <summary>M9 9D：讀進來的視窗設定不可信，Normalize 不能讓 NaN、負值或超大值往下傳。</summary>
public class WindowSettingsTests
{
    [Fact]
    public void ValidValues_PassThroughUnchanged()
    {
        var window = new WindowSettings(-1200, 40, 1040, 760, true, 1.5);

        Assert.Equal(window, window.Normalize());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    public void BadWidthOrHeight_BecomesZero(double bad)
    {
        var normalized = new WindowSettings(0, 0, bad, bad, false, 1.0).Normalize();

        Assert.Equal(0, normalized.Width);
        Assert.Equal(0, normalized.Height);
    }

    [Fact]
    public void HugeSize_IsCappedAtMaxSize()
    {
        var normalized = new WindowSettings(0, 0, 1e12, 1e12, false, 1.0).Normalize();

        Assert.Equal(WindowSettings.MaxSize, normalized.Width);
        Assert.Equal(WindowSettings.MaxSize, normalized.Height);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(100)]
    public void BadScaling_BecomesZero(double bad)
    {
        Assert.Equal(0, new WindowSettings(0, 0, 1040, 760, false, bad).Normalize().Scaling);
    }

    [Fact]
    public void HugeCoordinates_AreClamped()
    {
        var normalized = new WindowSettings(int.MaxValue, int.MinValue, 1040, 760, false, 1.0).Normalize();

        Assert.Equal(WindowSettings.MaxCoordinate, normalized.X);
        Assert.Equal(-WindowSettings.MaxCoordinate, normalized.Y);
    }
}
