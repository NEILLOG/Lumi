using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Views.Responsive;

/// <summary>
/// 把視窗的寬高分段換成根節點的 Classes（wide／compact／narrow、short／veryshort），樣式用 Selector 切換。
/// 只在跨越斷點時才改 Classes，拖曳中不會每個像素都重排。純視覺，所以放在 View 層，不經過 ViewModel。
/// </summary>
public static class ResponsiveClasses
{
    private static readonly string[] WidthNames = ["wide", "compact", "narrow"];
    private static readonly string[] HeightNames = ["short", "veryshort"];

    public static void Track(Window window)
    {
        var width = WidthClass.Wide;
        var height = HeightClass.Normal;
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
            hasSize = true;

            if (nextWidth == width && nextHeight == height && window.Classes.Count > 0)
                return;

            width = nextWidth;
            height = nextHeight;
            SetClass(window, WidthNames, WidthName(width));
            SetClass(window, HeightNames, HeightName(height));
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
