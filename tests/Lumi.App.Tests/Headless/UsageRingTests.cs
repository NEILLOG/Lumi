using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lumi.App.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Tests.Headless;

/// <summary>21B：用量圓環實際渲染（四種等級、Ratio 夾住、尺寸）。</summary>
public class UsageRingTests
{
    private static (UsageRing Ring, Window Window) Show(UsageRing ring)
    {
        var window = new Window { Width = 100, Height = 100, Content = ring };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
        return (ring, window);
    }

    [AvaloniaTheory]
    [InlineData(UsageLevel.Empty, 0)]
    [InlineData(UsageLevel.Normal, 0.0005)]
    [InlineData(UsageLevel.Normal, 0.42)]
    [InlineData(UsageLevel.Warning, 0.7)]
    [InlineData(UsageLevel.Danger, 1)]
    public void EachLevel_RendersWithoutException(UsageLevel level, double ratio)
    {
        var (ring, window) = Show(new UsageRing { Level = level, Ratio = ratio });

        ring.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();

        Assert.Equal(new Size(20, 20), ring.Bounds.Size);
        window.Close();
    }

    [AvaloniaFact]
    public void Ratio_IsClampedToZeroOne()
    {
        var ring = new UsageRing();

        ring.Ratio = 2;
        Assert.Equal(1, ring.Ratio);

        ring.Ratio = -0.5;
        Assert.Equal(0, ring.Ratio);

        ring.Ratio = double.NaN;
        Assert.Equal(0, ring.Ratio);
    }

    [AvaloniaFact]
    public void Diameter20_MeasuresAndArrangesTo20x20()
    {
        var ring = new UsageRing { Diameter = 20 };
        var (_, window) = Show(ring);

        Assert.Equal(new Size(20, 20), ring.DesiredSize);
        Assert.Equal(new Size(20, 20), ring.Bounds.Size);
        window.Close();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(0.005, 2)]
    [InlineData(0.5, 180)]
    [InlineData(2, 359.99)]
    public void SweepDegrees_FollowsRules(double ratio, double expected)
    {
        Assert.Equal(expected, UsageRing.SweepDegrees(ratio), 6);
    }
}
