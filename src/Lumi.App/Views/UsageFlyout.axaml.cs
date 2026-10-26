using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 用量面板（ui-usage-and-topbar.md 第 3 節）：寬 320。綁定的是工作台（<c>Usage.*</c>），畫面只讀、不改資料。
/// 按「開新對話」「繼續這則」或按 Esc 時發出 <see cref="Dismissed"/>，由 <see cref="ChatToolbar"/> 關掉面板並把焦點還給按鈕。
/// </summary>
public partial class UsageFlyout : UserControl
{
    public UsageFlyout()
    {
        InitializeComponent();
        // 隧道路由：Esc 在彈出層自己處理前先看到，才能把焦點還給觸發按鈕
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>面板要求關閉（Esc、開新對話、繼續這則）。</summary>
    public event EventHandler? Dismissed;

    private void OnDismissClick(object? sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        Dismissed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}

/// <summary>
/// 面板裡的小圓環（快取命中率）：和 <see cref="Lumi.App.Controls.UsageRing"/> 同一幾何（12 點鐘起順時針、圓頭線端），
/// 但進度色固定用成功色 <c>LumiSuccessBrush</c>（快取命中是「好」的意思，不跟著用量等級）。
/// </summary>
public sealed class UsageCacheRing : Control
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<UsageCacheRing, double>(nameof(Ratio), 0, coerce: (_, v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1));

    public static readonly StyledProperty<double> DiameterProperty =
        AvaloniaProperty.Register<UsageCacheRing, double>(nameof(Diameter), 18);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<UsageCacheRing, double>(nameof(Thickness), 3);

    private const double FullSweepLimit = 359.99;

    static UsageCacheRing()
    {
        AffectsRender<UsageCacheRing>(RatioProperty, ThicknessProperty);
        AffectsMeasure<UsageCacheRing>(DiameterProperty);
    }

    public UsageCacheRing()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    public double Diameter
    {
        get => GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
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
        if (side <= 0)
            return;

        var thickness = Math.Max(0.5, Thickness);
        var center = new Point(side / 2, side / 2);
        var radius = Math.Max(0, side / 2 - thickness / 2);

        context.DrawEllipse(null, new Pen(ResolveBrush("LumiBorderBrush"), thickness), center, radius, radius);

        if (Ratio <= 0)
            return;

        var sweep = Math.Min(Ratio * 360, FullSweepLimit);
        var progress = new Pen(ResolveBrush("LumiSuccessBrush"), thickness) { LineCap = PenLineCap.Round };
        context.DrawGeometry(null, progress, CreateArc(center, radius, sweep));
    }

    private static StreamGeometry CreateArc(Point center, double radius, double sweepDegrees)
    {
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

    private IBrush ResolveBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush found ? found : Brushes.Transparent;
}
