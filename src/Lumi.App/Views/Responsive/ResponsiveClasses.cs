using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Views.Responsive;

/// <summary>
/// 把視窗的寬高分段換成根節點的 Classes（wide／compact／narrow、short／veryshort），樣式用 Selector 切換。
/// 設定視窗另外可以開 <c>stacked</c>（寬度 &lt; 900，清單＋詳細改上下排，見 <see cref="ResponsiveRules.ClassifyStacked"/>）。
/// 只在跨越斷點時才改 Classes，拖曳中不會每個像素都重排。純視覺，所以放在 View 層，不經過 ViewModel。
/// </summary>
public static class ResponsiveClasses
{
    private static readonly string[] WidthNames = ["wide", "compact", "narrow"];
    private static readonly string[] HeightNames = ["short", "veryshort"];

    private const string StackedName = "stacked";

    /// <param name="window">掛在這個視窗的根節點上。</param>
    /// <param name="trackStacked">是否另外追蹤 <c>stacked</c>（只有設定視窗需要，工作台不開）。</param>
    public static void Track(Window window, bool trackStacked = false)
    {
        var width = WidthClass.Wide;
        var height = HeightClass.Normal;
        var stacked = false;
        var hasSize = false;

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.BoundsProperty)
                return;

            var size = window.Bounds;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var nextWidth = hasSize ? ResponsiveRules.ClassifyWidth(size.Width, width) : ResponsiveRules.ClassifyWidth(size.Width, WidthClass.Wide);
            var nextHeight = hasSize ? ResponsiveRules.ClassifyHeight(size.Height, height) : ResponsiveRules.ClassifyHeight(size.Height, HeightClass.Normal);
            var nextStacked = trackStacked && ResponsiveRules.ClassifyStacked(size.Width, hasSize && stacked);
            hasSize = true;

            if (nextWidth == width && nextHeight == height && nextStacked == stacked && window.Classes.Count > 0)
                return;

            width = nextWidth;
            height = nextHeight;
            stacked = nextStacked;
            SetClass(window, WidthNames, WidthName(width));
            SetClass(window, HeightNames, HeightName(height));
            if (trackStacked)
                window.Classes.Set(StackedName, stacked);
        };
    }

    private static string WidthName(WidthClass width) => width switch
    {
        WidthClass.Narrow => "narrow",
        WidthClass.Compact => "compact",
        _ => "wide",
    };

    private static string? HeightName(HeightClass height) => height switch
    {
        HeightClass.Short => "short",
        HeightClass.VeryShort => "veryshort",
        _ => null,
    };

    private static void SetClass(Window window, string[] names, string? current)
    {
        foreach (var name in names)
            window.Classes.Set(name, name == current);
    }
}
