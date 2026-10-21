using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Lumi.App.Tests.TestSupport;

/// <summary>一個實際渲染出來的文字：它的前景色、所在背景色（往上找第一個不透明的底，半透明的底依序疊合）與對比。</summary>
public sealed record TextContrastSample(string Text, string Owner, Color Foreground, Color Background, double Ratio)
{
    public override string ToString() => $"「{Text}」({Owner}) 前景 {Foreground} 背景 {Background} = {Ratio:0.00}:1";
}

/// <summary>
/// 走訪視覺樹，對每個可見的文字（TextBlock、TextBox 內的文字）計算前景色與它實際所在背景的對比。
/// 公式同 <see cref="Wcag"/>（與 <c>PaletteContrastTests</c> 相同）。不計：停用的按鈕／輸入元件裡的文字、placeholder（Watermark）、
/// 非單色的前景或背景（目前沒有）。
/// </summary>
public static class RenderedContrast
{
    public static IReadOnlyList<TextContrastSample> Measure(Visual root, out IReadOnlyList<string> problems)
    {
        var samples = new List<TextContrastSample>();
        var issues = new List<string>();
        foreach (var element in root.GetVisualDescendants().OfType<Control>())
        {
            var text = TextOf(element);
            if (string.IsNullOrWhiteSpace(text) || !element.IsEffectivelyVisible || element.Name == "PART_Watermark")
                continue;
            if (IsInsideDisabledInput(element))
                continue;

            var owner = $"{element.GetType().Name}#{element.Name}";
            var foreground = ForegroundOf(element);
            if (foreground is null)
            {
                issues.Add($"「{text}」({owner}) 沒有前景色");
                continue;
            }
            if (foreground is not ISolidColorBrush solid)
            {
                issues.Add($"「{text}」({owner}) 的前景不是單色（{foreground.GetType().Name}）");
                continue;
            }

            var background = BackgroundBehind(element, issues, text, owner);
            if (background is null)
                continue;

            var opacity = EffectiveOpacity(element) * solid.Opacity;
            var fg = Wcag.Over(solid.Color, background.Value, opacity);
            samples.Add(new TextContrastSample(text, owner, fg, background.Value, Wcag.Ratio(fg, background.Value)));
        }
        problems = issues;
        return samples;
    }

    private static string? TextOf(Control element) => element switch
    {
        TextBlock textBlock => textBlock.Text,
        TextPresenter presenter => presenter.Text,
        _ => null,
    };

    private static IBrush? ForegroundOf(Control element) => element switch
    {
        TextBlock textBlock => textBlock.Foreground,
        TextPresenter presenter => presenter.Foreground,
        _ => null,
    };

    private static IBrush? BackgroundOf(Visual visual) => visual switch
    {
        Border border => border.Background,
        Panel panel => panel.Background,
        TemplatedControl templated => templated.Background,
        ContentPresenter presenter => presenter.Background,
        TextBlock textBlock => textBlock.Background,
        _ => null,
    };

    /// <summary>文字在停用的按鈕、輸入框、核取方塊…裡（「停用元件不計」）。停用的清單項目（Menu 的「即將推出」）仍要量。</summary>
    private static bool IsInsideDisabledInput(Visual element) =>
        element.GetSelfAndVisualAncestors().OfType<Control>().Any(c =>
            (c is Button or TextBox or ComboBox or NumericUpDown) && !c.IsEffectivelyEnabled);

    private static double EffectiveOpacity(Visual element) =>
        element.GetSelfAndVisualAncestors().Aggregate(1.0, (acc, v) => acc * v.Opacity);

    private static Color? BackgroundBehind(Visual element, List<string> issues, string text, string owner)
    {
        var layers = new List<(Color Color, double Opacity)>();
        Color? floor = null;
        foreach (var visual in element.GetSelfAndVisualAncestors())
        {
            var brush = BackgroundOf(visual);
            if (brush is null)
                continue;
            if (brush is not ISolidColorBrush solid)
            {
                issues.Add($"「{text}」({owner}) 後面的背景不是單色（{visual.GetType().Name} 的 {brush.GetType().Name}）");
                return null;
            }
            var alpha = solid.Color.A / 255.0 * solid.Opacity;
            if (alpha <= 0)
                continue;
            if (alpha >= 1)
            {
                floor = Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B);
                break;
            }
            layers.Add((solid.Color, solid.Opacity));
        }
        if (floor is null)
        {
            issues.Add($"「{text}」({owner}) 往上找不到不透明的背景");
            return null;
        }

        var result = floor.Value;
        for (var i = layers.Count - 1; i >= 0; i--)   // 由底往上疊
            result = Wcag.Over(layers[i].Color, result, layers[i].Opacity);
        return result;
    }
}
