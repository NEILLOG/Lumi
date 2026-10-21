using Lumi.App.Views.Responsive;

namespace Lumi.App.Tests.Responsive;

/// <summary>斷點邊界與 hysteresis（RWD原則 第 2、5、8 節）：619/620、999/1000 不反覆切換。</summary>
public sealed class ResponsiveRulesTests
{
    [Theory]
    [InlineData(1200, WidthClass.Wide, WidthClass.Wide)]
    [InlineData(1000, WidthClass.Compact, WidthClass.Wide)]
    [InlineData(999, WidthClass.Compact, WidthClass.Compact)]
    [InlineData(992, WidthClass.Wide, WidthClass.Wide)]   // 已經是寬版：要退到 991 以下才切換
    [InlineData(991, WidthClass.Wide, WidthClass.Compact)]
    [InlineData(620, WidthClass.Compact, WidthClass.Compact)]
    [InlineData(619, WidthClass.Compact, WidthClass.Compact)]   // 已經是窄版中等：要到 611 以下才切換
    [InlineData(612, WidthClass.Compact, WidthClass.Compact)]
    [InlineData(611, WidthClass.Compact, WidthClass.Narrow)]
    [InlineData(619, WidthClass.Narrow, WidthClass.Narrow)]     // 從極窄回來：要到 628 才算中等
    [InlineData(628, WidthClass.Narrow, WidthClass.Compact)]
    [InlineData(500, WidthClass.Narrow, WidthClass.Narrow)]
    public void Width_UsesHysteresis_AroundEachBreakpoint(double width, WidthClass previous, WidthClass expected) =>
        Assert.Equal(expected, ResponsiveRules.ClassifyWidth(width, previous));

    [Fact]
    public void Width_DoesNotFlipBackAndForth_WhenResizingAcrossBoundary()
    {
        // 從 1200 慢慢縮到 500 再慢慢放大：每個邊界只換一次
        var changes = new List<WidthClass>();
        var current = WidthClass.Wide;
        var widths = Enumerable.Range(0, 800).Select(i => 1200.0 - i).Concat(Enumerable.Range(0, 800).Select(i => 500.0 + i));
        foreach (var w in widths)
        {
            var next = ResponsiveRules.ClassifyWidth(w, current);
            if (next != current)
            {
                changes.Add(next);
                current = next;
            }
        }
        Assert.Equal([WidthClass.Compact, WidthClass.Narrow, WidthClass.Compact, WidthClass.Wide], changes);
    }

    [Theory]
    [InlineData(800, HeightClass.Normal, HeightClass.Normal)]
    [InlineData(700, HeightClass.Normal, HeightClass.Normal)]
    [InlineData(699, HeightClass.Normal, HeightClass.Short)]
    [InlineData(707, HeightClass.Short, HeightClass.Short)]   // 已經是矮版：要到 708 才回到一般
    [InlineData(708, HeightClass.Short, HeightClass.Normal)]
    [InlineData(600, HeightClass.Normal, HeightClass.Short)]
    [InlineData(599, HeightClass.Normal, HeightClass.VeryShort)]
    [InlineData(607, HeightClass.VeryShort, HeightClass.VeryShort)]
    [InlineData(608, HeightClass.VeryShort, HeightClass.Short)]
    public void Height_UsesHysteresis_AroundEachBreakpoint(double height, HeightClass previous, HeightClass expected) =>
        Assert.Equal(expected, ResponsiveRules.ClassifyHeight(height, previous));

    [Theory]
    [InlineData(1040, false, false)]
    [InlineData(900, false, false)]   // 剛好 900 還是左右排
    [InlineData(899, false, true)]
    [InlineData(720, false, true)]
    [InlineData(907, true, true)]     // 已經是上下排：要到 908 才回到左右排
    [InlineData(908, true, false)]
    [InlineData(1200, true, false)]
    public void Stacked_BelowNineHundred_UsesHysteresis(double width, bool previous, bool expected) =>
        Assert.Equal(expected, ResponsiveRules.ClassifyStacked(width, previous));

    [Fact]
    public void Stacked_DoesNotFlipBackAndForth_WhenResizingAcrossBoundary()
    {
        var changes = new List<bool>();
        var current = false;
        var widths = Enumerable.Range(0, 400).Select(i => 1100.0 - i).Concat(Enumerable.Range(0, 400).Select(i => 700.0 + i));
        foreach (var w in widths)
        {
            var next = ResponsiveRules.ClassifyStacked(w, current);
            if (next != current)
            {
                changes.Add(next);
                current = next;
            }
        }
        Assert.Equal([true, false], changes);
    }
}
