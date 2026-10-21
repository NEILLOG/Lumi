using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Models;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-2：設定視窗實際渲染出來的文字，在五套主題 × 淺／深下，前景色與它所在背景的對比 ≥ 4.5:1（plan/12 驗收標準第 6 條）。
/// 與 <c>PaletteContrastTests</c>（直接讀色票檔）互補：這裡量的是元件樣式真正套出來的顏色。
/// </summary>
public class SettingsContrastTests
{
    /// <summary>
    /// 已知例外（token 問題，不為了通過而改色票）：標題列品牌方塊裡的「L」。它是 Accent 底上的 OnAccent 字標（品牌標誌，視為裝飾符號），
    /// 色碼照 ui-guide.html 的 Accent：sage／teal／mist-blue 的淺色變體只有 3.85／4.07／4.28:1。
    /// 這個組合只要求非文字對比 ≥ 3:1（<c>PaletteContrastTests.AccentIcon_OnAccent_MeetsNonTextThreshold_InEveryTheme</c> 同一個下限）。
    /// 設定 Menu 標題區的「L」是 Soft 底＋AccentText，沒有這個問題，仍照 4.5:1 檢查。
    /// </summary>
    private static bool IsBrandMarkOnAccent(TextContrastSample sample, Color accent) =>
        sample.Text == Strings.TitleBar_LogoMark && sample.Background == Color.FromRgb(accent.R, accent.G, accent.B);

    private const double BrandMarkMinimum = 3.0;

    public static TheoryData<string> Pages()
    {
        var data = new TheoryData<string>();
        foreach (var page in SettingsRig.Pages)
            data.Add(page);
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public async Task EveryVisibleText_MeetsTheTextContrast_InFivePalettesAndBothVariants(string page)
    {
        using var rig = await SettingsRig.CreateAsync(page);
        var failures = new List<string>();
        var accents = new HashSet<Color>();
        try
        {
            foreach (var palette in ThemePalettes.All)
            {
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    rig.ApplyTheme(palette, variant);

                    // 主題真的套到視窗上了（而不是只換了 Application 的資源）：Menu 的 Chrome 底色等於該主題的 token
                    Assert.True(Application.Current!.TryGetResource("LumiChromeBrush", rig.Window.ActualThemeVariant, out var chrome));
                    var menu = rig.Window.FindByName<Avalonia.Controls.Border>("Menu");
                    Assert.Equal(((ISolidColorBrush)chrome!).Color, ((ISolidColorBrush)menu.Background!).Color);
                    Assert.True(Application.Current.TryGetResource("LumiAccentBrush", rig.Window.ActualThemeVariant, out var accent));
                    if (variant == ThemeVariant.Light)
                        accents.Add(((ISolidColorBrush)accent!).Color);

                    var samples = RenderedContrast.Measure(rig.Window, out var problems);
                    Assert.True(samples.Count > 20, $"{page} 只量到 {samples.Count} 段文字");
                    foreach (var problem in problems)
                        failures.Add($"{palette} {variant} {page}：{problem}");
                    var accentColor = ((ISolidColorBrush)accent!).Color;
                    foreach (var sample in samples.Where(s => s.Ratio < Wcag.TextMinimum))
                    {
                        if (IsBrandMarkOnAccent(sample, accentColor) && sample.Ratio >= BrandMarkMinimum)
                            continue;
                        failures.Add($"{palette} {variant} {page}：{sample}");
                    }
                }
            }
        }
        finally
        {
            SettingsRig.ResetTheme();
        }

        Assert.Equal(ThemePalettes.All.Count, accents.Count);   // 五套的 Accent 都不同 → 迴圈真的換了色票
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(60)));
    }
}
