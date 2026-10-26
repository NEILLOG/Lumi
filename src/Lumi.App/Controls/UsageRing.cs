using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Lumi.App.ViewModels;

namespace Lumi.App.Controls;

/// <summary>
/// 用量圓環（ui-usage-and-topbar.md 2.1、2.3）：軌道整圈，進度弧從 12 點鐘方向順時針畫，圓頭線端。
/// 顏色只用主題 token（<c>LumiBorderBrush</c>、<c>LumiAccentBrush</c>、<c>LumiWarningIconBrush</c>、<c>LumiDangerBrush</c>、
/// <c>LumiTextMutedBrush</c>），不寫死色碼。純繪製元件：不可聚焦、不吃滑鼠。
/// </summary>
public sealed class UsageRing : Control
{
    /// <summary>進度最小顯示角度（度）：0 &lt; Ratio &lt; 1% 時至少畫這麼多，避免圓頭線端看不見。</summary>
    public const double MinSweepDegrees = 2;

    /// <summary>低於這個比例時才套用最小角度。</summary>
    public const double MinSweepThreshold = 0.01;

    /// <summary>補間時間（ui-usage-and-topbar.md 2.3）。</summary>
    public static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(200);

    private const double FullSweepLimit = 359.99;
    private static readonly double[] EmptyDashes = [2.5, 3.2];

    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<UsageRing, double>(
            nameof(Ratio),
            defaultValue: 0,
            coerce: (_, value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1));

    public static readonly StyledProperty<UsageLevel> LevelProperty =
        AvaloniaProperty.Register<UsageRing, UsageLevel>(nameof(Level), UsageLevel.Empty);

    public static readonly StyledProperty<double> DiameterProperty =
        AvaloniaProperty.Register<UsageRing, double>(nameof(Diameter), 20);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<UsageRing, double>(nameof(Thickness), 2.5);

    /// <summary>實際畫出來的比例（0–1）；由 <see cref="RatioProperty"/> 變更時以 200ms 補間。</summary>
    public static readonly StyledProperty<double> DisplayRatioProperty =
        AvaloniaProperty.Register<UsageRing, double>(
            nameof(DisplayRatio),
            defaultValue: 0,
            coerce: (_, value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1));

    static UsageRing()
    {
        AffectsRender<UsageRing>(LevelProperty, ThicknessProperty, DisplayRatioProperty);
        AffectsMeasure<UsageRing>(DiameterProperty);
        AffectsRender<UsageRing>(DiameterProperty);
        RatioProperty.Changed.AddClassHandler<UsageRing>((ring, e) => ring.DisplayRatio = e.GetNewValue<double>());
    }

    public UsageRing()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = DisplayRatioProperty,
                Duration = AnimationDuration,
                Easing = new QuadraticEaseOut(),
            },
        };
    }

    /// <summary>進度 0..1；超出範圍會夾住，NaN 當 0。</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    /// <summary>等級，決定進度弧顏色；<see cref="UsageLevel.Empty"/> 只畫虛線軌道。</summary>
    public UsageLevel Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>外框直徑（同時是控制項的寬與高）。</summary>
    public double Diameter
    {
        get => GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    /// <summary>線寬（Empty 時固定用 2）。</summary>
    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>目前畫出來的比例（補間中間值）。通常由 <see cref="Ratio"/> 驅動，不需要直接設定。</summary>
    public double DisplayRatio
    {
        get => GetValue(DisplayRatioProperty);
        set => SetValue(DisplayRatioProperty, value);
    }

    /// <summary>
    /// 進度弧的掃過角度（度）。0 表示不畫；0 &lt; ratio &lt; 1% 時至少 <see cref="MinSweepDegrees"/>；上限略小於 360，避免弧頭與弧尾重合消失。
    /// </summary>
    public static double SweepDegrees(double ratio)
    {
        if (double.IsNaN(ratio) || ratio <= 0)
            return 0;
        var clamped = Math.Min(ratio, 1);
        var degrees = clamped * 360;
        if (clamped < MinSweepThreshold)
            degrees = Math.Max(degrees, MinSweepDegrees);
        return Math.Min(degrees, FullSweepLimit);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsFinite(Diameter) && Diameter > 0 ? Diameter : 0;
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0 || Diameter <= 0)
            return;

        var center = new Point(side / 2, side / 2);
        var isEmpty = Level == UsageLevel.Empty;
        var thickness = isEmpty ? 2 : Math.Max(0.5, Thickness);
        var radius = Math.Max(0, side / 2 - thickness / 2);

        var trackPen = isEmpty
            ? new Pen(ResolveBrush("LumiTextMutedBrush", Brushes.Transparent, 0.5), thickness, new DashStyle(EmptyDashes, 0))
            : new Pen(ResolveBrush("LumiBorderBrush", Brushes.Transparent), thickness);
        context.DrawEllipse(null, trackPen, center, radius, radius);

        if (isEmpty)
            return;

        var sweep = SweepDegrees(DisplayRatio);
        if (sweep <= 0)
            return;

        var progressPen = new Pen(ResolveBrush(LevelBrushKey(Level), Brushes.Transparent), thickness) { LineCap = PenLineCap.Round };
        context.DrawGeometry(null, progressPen, CreateArc(center, radius, sweep));
    }

    private static StreamGeometry CreateArc(Point center, double radius, double sweepDegrees)
    {
        // 12 點鐘為 -90 度；螢幕座標 y 向下，角度增加即順時針。
        var startAngle = -90 * Math.PI / 180;
        var endAngle = (-90 + sweepDegrees) * Math.PI / 180;
        var start = new Point(center.X + radius * Math.Cos(startAngle), center.Y + radius * Math.Sin(startAngle));
        var end = new Point(center.X + radius * Math.Cos(endAngle), center.Y + radius * Math.Sin(endAngle));

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(start, false);
        ctx.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return geometry;
    }

    private static string LevelBrushKey(UsageLevel level) => level switch
    {
        UsageLevel.Warning => "LumiWarningIconBrush",
        UsageLevel.Danger => "LumiDangerBrush",
        _ => "LumiAccentBrush",
    };

    /// <summary>從主題資源取色（跟著目前的主題變體）；找不到時退回 fallback，且 opacity 會套用在結果上。</summary>
    private IBrush ResolveBrush(string key, IBrush fallback, double opacity = 1)
    {
        var brush = this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush found ? found : fallback;
        if (opacity >= 1)
            return brush;
        return brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;
    }
}
