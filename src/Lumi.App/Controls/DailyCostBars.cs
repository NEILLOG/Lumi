using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumi.App.Resources;
using Lumi.Core.Usage;

namespace Lumi.App.Controls;

/// <summary>
/// 設定 › 用量與費用的「每天」長條圖（plan/23-usage-cost.md 第 6 節第 3 段）：每天一根，平均分配寬度、間距 2；
/// 本月最高的那天滿高；0 元的日子畫 2px 短條（<c>LumiBorderBrush</c>）；今天用 <c>LumiAccentHoverBrush</c>，其他日用 <c>LumiAccentBrush</c>；
/// 未來的日子不畫。滑鼠停留顯示「日期 · 金額」。顏色只用主題 token（同 <see cref="UsageRing"/> 的取色寫法）。
/// </summary>
public sealed class DailyCostBars : Control
{
    /// <summary>圖的高度（不含 ToolTip）。</summary>
    public const double BarHeight = 96;

    /// <summary>相鄰兩根長條的間距。</summary>
    public const double Gap = 2;

    /// <summary>0 元或很小的日子畫的最低高度。</summary>
    public const double MinBarHeight = 2;

    public static readonly StyledProperty<IReadOnlyList<DailyCost>?> ItemsProperty =
        AvaloniaProperty.Register<DailyCostBars, IReadOnlyList<DailyCost>?>(nameof(Items));

    static DailyCostBars()
    {
        AffectsRender<DailyCostBars>(ItemsProperty);
    }

    public DailyCostBars()
    {
        Height = BarHeight;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>每天一筆，依日期由小到大（1 日到月底）。</summary>
    public IReadOnlyList<DailyCost>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var items = Items;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (items is null || items.Count == 0 || width <= 0 || height <= 0)
            return;

        var count = items.Count;
        var barWidth = Math.Max(1, (width - Gap * (count - 1)) / count);
        var max = items.Where(i => !i.IsFuture).Select(i => i.Usd).DefaultIfEmpty(0m).Max();
        var idleBrush = ResolveBrush("LumiBorderBrush");
        var accentBrush = ResolveBrush("LumiAccentBrush");
        var todayBrush = ResolveBrush("LumiAccentHoverBrush");

        for (var i = 0; i < count; i++)
        {
            var day = items[i];
            if (day.IsFuture)
                continue;

            var isZero = day.Usd <= 0 || max <= 0;
            var barHeight = isZero
                ? MinBarHeight
                : Math.Max(MinBarHeight, (double)(day.Usd / max) * height);
            var brush = isZero ? idleBrush : day.IsToday ? todayBrush : accentBrush;
            var rect = new Rect(i * (barWidth + Gap), height - barHeight, barWidth, barHeight);
            context.DrawRectangle(brush, null, new RoundedRect(rect, new CornerRadius(2)));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ToolTip.SetTip(this, TipAt(e.GetPosition(this).X));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, null);
    }

    /// <summary>滑鼠 x 位置所在那天的提示文字；未來的日子與沒有資料時回 null。</summary>
    private string? TipAt(double x)
    {
        var items = Items;
        if (items is null || items.Count == 0 || Bounds.Width <= 0)
            return null;

        var slot = Bounds.Width / items.Count;
        var index = Math.Clamp((int)(x / slot), 0, items.Count - 1);
        var day = items[index];
        if (day.IsFuture)
            return null;

        return string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_DailyTooltipFormat,
            day.Date.ToString("M/d", CultureInfo.InvariantCulture), MoneyFormat.Twd(day.Usd));
    }

    /// <summary>從主題資源取色（跟著目前的主題變體）；找不到時退回透明。</summary>
    private IBrush ResolveBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
}
