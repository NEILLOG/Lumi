using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Models;
using Xunit.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-4：全域顏色殘留檢查。在五套主題 × 淺／深下，走訪工作台、設定視窗（所有畫面狀態）、模型選單、工具卡片、ConfirmDialog、小精靈視窗的視覺樹，
/// 每個看得見的元素實際生效的前景／背景／框線／填色，若不是 Lumi token（Themes/ 底下任何 Lumi* 色票或語意色，或透明）就是 Fluent 預設色殘留，列出清單。
/// 不可避免的例外寫在 <see cref="Exceptions"/>，並註明原因。
/// </summary>
public class FluentColorResidueTests
{
    private readonly ITestOutputHelper _output;

    public FluentColorResidueTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 不可避免的例外（每一條都要有原因）。回傳 true 表示這個元素的顏色不計。
    /// </summary>
    private static readonly (string Reason, Func<Visual, bool> Matches)[] Exceptions =
    [
        // 捲軸上下箭頭按鈕（RepeatButton 與裡面的 PathIcon）：只有捲軸展開（滑鼠移到捲軸上）時才出現，顏色由 Fluent 依淺／深色主題給前景（黑／白），
        // 在 Lumi 的淺深色底上都可讀；計畫只要求調整 thumb 與 track（BorderStrong／透明），其餘不動。
        ("ScrollBar 上下箭頭（Fluent，僅展開時出現）", v => v.GetVisualAncestors().OfType<RepeatButton>().Any(b => b.TemplatedParent is ScrollBar)),
        // 視窗模板的 PART_TransparencyFallback：視窗不支援透明效果時的底，畫在視窗 Background 的後面，被 Lumi 的不透明視窗底色完全蓋住，使用者看不到。
        ("Window 模板的 PART_TransparencyFallback（被視窗底色蓋住）", v => v is Border { Name: "PART_TransparencyFallback" }),
        // 設定「外觀與操作」的色票卡片：色塊（Border.swatch）顯示的是「那一套色票自己的」Accent／Hover／Soft／Text，所以一定不是目前主題的 token；
        // 顏色來自 Themes/Palettes（PalettePreview 讀檔，C# 沒有複製色碼）。
        ("色票卡片的色塊（顯示各套色票自己的顏色）", v => v is Border b && (b.Classes.Contains("swatch") || b.GetVisualAncestors().OfType<Border>().Any(a => a.Classes.Contains("swatch")))),
    ];

    private static void Collect(IResourceProvider? provider, ThemeVariant variant, HashSet<Color> colors)
    {
        switch (provider)
        {
            case ResourceInclude include:
                Collect(include.Loaded, variant, colors);
                break;
            case ResourceDictionary dictionary:
                foreach (var pair in dictionary)
                {
                    if (pair.Key is not string key || !key.StartsWith("Lumi", StringComparison.Ordinal))
                        continue;
                    if (pair.Value is ISolidColorBrush brush)
                        colors.Add(brush.Color);
                    else if (pair.Value is Color color)
                        colors.Add(color);
                }
                foreach (var merged in dictionary.MergedDictionaries)
                    Collect(merged, variant, colors);
                if (dictionary.ThemeDictionaries.TryGetValue(variant, out var themed))
                    Collect(themed, variant, colors);
                break;
        }
    }

    /// <summary>目前這套色票與明暗下，所有 Lumi token 的顏色（含 Dot、Code、語意色），加上透明。</summary>
    private static HashSet<Color> TokenColors(ThemeVariant variant)
    {
        var colors = new HashSet<Color> { Colors.Transparent };
        Collect(Application.Current!.Resources, variant, colors);
        return colors;
    }

    private static IEnumerable<(string Property, IBrush? Brush)> PaintedBrushes(Visual visual)
    {
        switch (visual)
        {
            case Border border:
                yield return ("Background", border.Background);
                if (border.BorderThickness != default)   // 框線粗細 0 就看不到，不計
                    yield return ("BorderBrush", border.BorderBrush);
                break;
            case ContentPresenter presenter:
                yield return ("Background", presenter.Background);
                if (presenter.BorderThickness != default)
                    yield return ("BorderBrush", presenter.BorderBrush);
                break;
            case TextBlock text:
                yield return ("Foreground", text.Foreground);
                yield return ("Background", text.Background);
                break;
            case TextPresenter presenter:
                yield return ("Foreground", presenter.Foreground);
                break;
            case Shape shape:
                yield return ("Fill", shape.Fill);
                yield return ("Stroke", shape.Stroke);
                break;
            case LumiIcon icon:
                yield return ("Foreground", icon.Foreground);
                break;
            case Panel panel:
                yield return ("Background", panel.Background);
                break;
            case PathIcon pathIcon:
                yield return ("Foreground", pathIcon.Foreground);
                break;
        }
    }

    /// <summary>Window 的 Foreground／Background 在正式 App 由 App.axaml 的 Window 樣式設成 token；測試用的 TestApp 沒有，這裡照樣補上。</summary>
    private static void ApplyAppWindowStyle(Window window)
    {
        Assert.True(Application.Current!.TryGetResource("LumiTextBrush", window.ActualThemeVariant, out var text));
        Assert.True(Application.Current.TryGetResource("LumiBackgroundBrush", window.ActualThemeVariant, out var background));
        window.Foreground = (IBrush)text!;
        window.Background = (IBrush)background!;
    }

    private static string Describe(IBrush? brush) => brush switch
    {
        null => "null",
        ISolidColorBrush s => s.Color.ToString() + (s.Opacity < 1 ? $"@{s.Opacity:0.##}" : ""),
        _ => brush.GetType().Name,
    };

    [AvaloniaFact]
    public async Task NoVisibleElement_UsesAFluentDefaultColor_InFivePalettesAndBothVariants()
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        var findings = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var scanned = 0;
        try
        {
            foreach (var palette in ThemePalettes.All)
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                ThemeService.ApplyPalette(Application.Current!.Resources.MergedDictionaries, palette);
                foreach (var (surface, window) in surfaces.Windows)
                {
                    window.RequestedThemeVariant = variant;
                    window.UpdateLayout();
                    ApplyAppWindowStyle(window);
                    window.UpdateLayout();
                    var tokens = TokenColors(window.ActualThemeVariant);
                    Assert.True(tokens.Count > 30, $"只收集到 {tokens.Count} 個 token 顏色");

                    foreach (var visual in new[] { (Visual)window }.Concat(window.GetVisualDescendants()))
                    {
                        if (visual is not Control control || !control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                            continue;
                        if (Exceptions.Any(e => e.Matches(visual)))
                            continue;
                        scanned++;
                        foreach (var (property, brush) in PaintedBrushes(visual))
                        {
                            if (brush is null)
                                continue;
                            if (brush is not ISolidColorBrush solid)
                            {
                                findings.TryAdd($"{control.GetType().Name}#{control.Name}.{property} 不是單色（{brush.GetType().Name}）", []).GetHashCode();
                                continue;
                            }
                            if (tokens.Contains(solid.Color))
                                continue;
                            var key = $"{control.GetType().Name}#{control.Name}.{property} = {Describe(solid)}（上層：{Path(control)}）";
                            if (!findings.TryGetValue(key, out var where))
                                findings[key] = where = [];
                            where.Add($"{surface} {palette} {variant}");
                        }
                    }
                }
            }
        }
        finally
        {
            ThemeService.ApplyPalette(Application.Current!.Resources.MergedDictionaries, ThemePalettes.Default);
        }

        _output.WriteLine($"掃描 {scanned} 個元素，殘留 {findings.Count} 種");
        foreach (var (key, where) in findings.Take(40))
            _output.WriteLine($"{key}  ← {where.Count} 處，例如 {where.First()}");
        Assert.True(scanned > 5000, $"只掃到 {scanned} 個元素，畫面清單可能不完整");
        Assert.True(findings.Count == 0, "以下元素仍使用 Fluent 預設色（不是 Lumi token）：\n" + string.Join("\n", findings.Take(40).Select(f => $"{f.Key} ← 例如 {f.Value.First()}")));
    }

    /// <summary>往上最近的兩層有名字或型別特別的祖先，方便定位。</summary>
    private static string Path(Control control)
    {
        var parts = control.GetVisualAncestors().OfType<Control>()
            .Where(c => c is not (Border or Panel or ContentPresenter or Decorator or ContentControl { Content: null }))
            .Take(3)
            .Select(c => $"{c.GetType().Name}{(c.Name is { Length: > 0 } n ? "#" + n : "")}");
        return string.Join(" < ", parts);
    }

    [AvaloniaFact]
    public void TheExceptionList_IsShort_AndEveryEntryHasAReason()
    {
        // 例外清單刻意很短：新增例外要在這裡同步並寫原因
        Assert.Equal(3, Exceptions.Length);
        Assert.All(Exceptions, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
    }
}
