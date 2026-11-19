using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Lumi.App.Resources;
using Lumi.Core.Usage;

namespace Lumi.App.Controls;

/// <summary>
/// 設定 › 用量與費用的「每天」長條圖（plan/23-usage-cost.md 第 6 節第 3 段）：每天一根，平均分配寬度、間距 2；
/// 本月最高的那天滿高；0 元的日子畫 2px 短條（<c>LumiBorderBrush</c>）；今天用 <c>LumiAccentHoverBrush</c>，其他日用 <c>LumiAccentBrush</c>；
/// 未來的日子不畫。滑鼠停留顯示「日期 · 金額」：每天是一整欄（含長條上方的空白），整個控制項的範圍都能停留（<see cref="HitTest"/>）。
/// 顏色只用主題 token（同 <see cref="UsageRing"/> 的取色寫法）。
/// </summary>
public sealed class DailyCostBars : Control, ICustomHitTest
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
        // 資料換了，游標下的那天也跟著換；清掉記住的天，下一次移動才會重設提示
        ItemsProperty.Changed.AddClassHandler<DailyCostBars>((bars, _) => bars._tipIndex = -1);
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

    /// <summary>
    /// 整個控制項的矩形都算命中，不只畫出來的長條像素（空白、0 元與矮長條都要能停留）。
    /// 用 ICustomHitTest 而不是畫一層透明底：不靠畫圖結果，也不會因為改了畫法而失效（同 WorkbenchSpriteHost 的寫法）。
    /// </summary>
    public bool HitTest(Point point) =>
        point.X >= 0 && point.Y >= 0 && point.X < Bounds.Width && point.Y < Bounds.Height;

    // 目前提示對應的那一天（-1 = 沒有提示）。同一天移動時不重設提示，避免提示閃爍。
    private int _tipIndex = -1;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        // Avalonia 的 ToolTip 服務在移動事件「之前」判斷要不要開提示，所以進入時就先設一次
        UpdateTip(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateTip(e.GetPosition(this).X);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _tipIndex = -1;
        ToolTip.SetTip(this, null);
    }

    /// <summary>只在換到不同天時才更新提示內容（已開著的提示換到另一天，設定新的文字即可）。</summary>
    private void UpdateTip(double x)
    {
        var index = DayIndexAt(x);
        if (index == _tipIndex)
            return;

        _tipIndex = index;
        ToolTip.SetTip(this, index < 0 ? null : TipFor(index));
    }

    /// <summary>滑鼠 x 位置所在那天的索引（每天一欄，欄寬 = 寬 / 天數）；沒有資料或不在範圍內時回 -1。</summary>
    private int DayIndexAt(double x)
    {
        var items = Items;
        if (items is null || items.Count == 0 || Bounds.Width <= 0 || x < 0 || x >= Bounds.Width)
            return -1;

        var slot = Bounds.Width / items.Count;
        var index = Math.Clamp((int)(x / slot), 0, items.Count - 1);
        return items[index].IsFuture ? -1 : index;
    }

    /// <summary>某天的提示文字「M/d · 金額」（金額 0 的日子也有）。</summary>
    private string TipFor(int index)
    {
        var day = Items![index];
        return string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_DailyTooltipFormat,
            day.Date.ToString("M/d", CultureInfo.InvariantCulture), MoneyFormat.Twd(day.Usd));
    }

    /// <summary>從主題資源取色（跟著目前的主題變體）；找不到時退回透明。</summary>
    private IBrush ResolveBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
}
