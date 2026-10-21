using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Lumi.App.Controls;

/// <summary>
/// 把子元素的寬度限制在「可用寬度的某個比例」與「固定像素上限」兩者較小的那個（RWD原則 3.3：Avalonia 沒有百分比 MaxWidth）。
/// 可用寬度就是它所在版面（對話區內容寬度）給它的寬度，視窗縮放時由版面系統重新計算，不需要監聽 Bounds，也不經過 ViewModel。
/// 比例與上限用 Style 依根節點的 wide／compact／narrow class 設定。子元素的 HorizontalAlignment（靠左或靠右）照常生效。
/// </summary>
public sealed class FractionWidthLimiter : Decorator
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<FractionWidthLimiter, double>(nameof(Fraction), 1.0);

    public static readonly StyledProperty<double> MaxPixelsProperty =
        AvaloniaProperty.Register<FractionWidthLimiter, double>(nameof(MaxPixels), double.PositiveInfinity);

    static FractionWidthLimiter()
    {
        AffectsMeasure<FractionWidthLimiter>(FractionProperty, MaxPixelsProperty);
    }

    /// <summary>可用寬度的比例（0～1）。</summary>
    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>固定像素上限；沒有上限就是無限大。</summary>
    public double MaxPixels
    {
        get => GetValue(MaxPixelsProperty);
        set => SetValue(MaxPixelsProperty, value);
    }

    /// <summary>可用寬度套用比例與上限之後的寬度。</summary>
    public static double Limit(double available, double fraction, double maxPixels) =>
        double.IsInfinity(available) ? maxPixels : Math.Min(maxPixels, available * fraction);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
            return default;

        Child.Measure(new Size(Limit(availableSize.Width, Fraction, MaxPixels), availableSize.Height));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null)
            return finalSize;

        var limit = Limit(finalSize.Width, Fraction, MaxPixels);
        var width = Child.HorizontalAlignment == HorizontalAlignment.Stretch
            ? Math.Min(finalSize.Width, limit)
            : Math.Min(Child.DesiredSize.Width, limit);
        var x = Child.HorizontalAlignment switch
        {
            HorizontalAlignment.Right => finalSize.Width - width,
            HorizontalAlignment.Center => (finalSize.Width - width) / 2,
            _ => 0,
        };
        Child.Arrange(new Rect(x, 0, width, finalSize.Height));
        return finalSize;
    }
}
