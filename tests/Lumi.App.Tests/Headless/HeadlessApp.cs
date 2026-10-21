using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Lumi.App.Tests.Headless;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Lumi.App.Tests.Headless;

/// <summary>
/// 不開真的視窗的 Avalonia 環境（Avalonia.Headless）：用來測渲染出來的控制項結構、增量更新、
/// 連結的命中測試。載入和正式程式相同的主題 token、字型資源與 Markdown 樣式。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/Tokens.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/Typography.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/Metrics.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/Icons.axaml") });
        // 與正式 App 相同：預設色票由 ThemeService 套用
        Lumi.App.Services.ThemeService.ApplyPalette(Resources.MergedDictionaries, Lumi.Core.Models.ThemePalettes.Default);
        Styles.Add(new StyleInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/Controls.axaml") });
        Styles.Add(new StyleInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/Markdown.axaml") });
    }
}
