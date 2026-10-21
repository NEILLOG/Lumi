using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Lumi.App.Controls;

/// <summary>
/// 線條圖示：把 <c>Themes/Icons.axaml</c> 的 24x24 幾何（<c>LumiIconXxx</c>）畫成描邊圖示。
/// 描邊色是 <see cref="Foreground"/>（和 TextBlock 一樣會從父層繼承，所以放在按鈕裡會跟著按鈕的文字色，
/// 停用、Hover 時也一起變）。線寬 <see cref="StrokeThickness"/> 是 24x24 座標裡的值，會跟著 <see cref="Size"/> 縮放，
/// 所以 16px 的圖示看起來和 24px 的圖示粗細比例一致。
/// 這是純繪製元件：不可聚焦、不吃滑鼠（點擊交給外層按鈕）。
/// </summary>
public class LumiIcon : Control
{
    /// <summary>圖示座標系的邊長（Lucide 是 24x24）。</summary>
    public const double ViewBoxSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LumiIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<LumiIcon, double>(nameof(Size), 16d);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LumiIcon, double>(nameof(StrokeThickness), 1.75d);

    /// <summary>與 TextBlock／Button 共用同一個繼承屬性，所以沒設定時會用父層的文字色。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LumiIcon>();

    static LumiIcon()
    {
        AffectsMeasure<LumiIcon>(SizeProperty);
        AffectsRender<LumiIcon>(DataProperty, SizeProperty, StrokeThicknessProperty, ForegroundProperty);
        FocusableProperty.OverrideDefaultValue<LumiIcon>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<LumiIcon>(false);
        HorizontalAlignmentProperty.OverrideDefaultValue<LumiIcon>(Avalonia.Layout.HorizontalAlignment.Center);
        VerticalAlignmentProperty.OverrideDefaultValue<LumiIcon>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>顯示邊長（像素），預設 16。</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>線寬（24x24 座標），預設 1.75。</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    /// <summary>圖示實際使用的描邊（圓角線帽與線接合）；沒有前景色時是 null。</summary>
    internal IPen? CreatePen() =>
        Foreground is { } brush
            ? new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Data is not { } geometry || CreatePen() is not { } pen || Size <= 0)
            return;

        var scale = Size / ViewBoxSize;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, geometry);
    }
}
