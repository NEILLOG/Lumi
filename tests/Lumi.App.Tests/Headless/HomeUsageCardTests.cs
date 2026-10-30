using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.App.Views.Responsive;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M23 23L：設定首頁「本月用量」卡片（<c>HomePanel</c>）的金額、預算列與進度條——沒設預算、有預算的三種等級、
/// 「管理 ›」按鈕、沒有用量紀錄來源時的 tokens、五套主題 × 淺深色的文字對比。
/// </summary>
public class HomeUsageCardTests
{
    private static readonly string[] BarBrushKeys = ["LumiAccentBrush", "LumiWarningIconBrush", "LumiDangerBrush"];

    /// <summary>獨立視窗：掛上 Track（寬度 class 與 stacked），內容是首頁。與 <see cref="HomePanelTests"/> 相同的設定。</summary>
    private static Window Host(SettingsViewModel settings, double width = 1040)
    {
        var window = new Window { Width = width, Height = 900, Content = new HomePanel { DataContext = settings } };
        window.Styles.Add(new StyleInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/ChatStyles.axaml") });
        ResponsiveClasses.Track(window, trackStacked: true);
        window.Show();
        Relayout(window);
        return window;
    }

    private static void Relayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Border Card(Window window) => Named<Border>(window, "HomeUsageCard");

    private static TextBlock BigNumber(Window window) =>
        Card(window).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("big-number"));

    private static TextBlock TokensUnit(Window window) =>
        Card(window).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.Usage_Tokens);

    private static IReadOnlyList<ProgressBar> Bars(Window window) => Card(window).GetVisualDescendants().OfType<ProgressBar>().ToList();

    private static string Format(string format, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);

    private static UsageRecord UsageAt(DateTimeOffset at, decimal cost) =>
        new(UsageRecord.NewId(), at, "conv", UsagePurpose.Chat, Lumi.Core.Chat.ChatMode.Workbench, "ms", "model", "conn",
            Lumi.Core.Chat.TokenUsage.Zero, false, null, cost, 0);

    /// <summary>本月 10 月 5 日一筆 <paramref name="usd"/> 美元（今天是 10/19，所以今天的金額是 0）。</summary>
    private static InMemoryUsageStore SpentThisMonth(decimal usd)
    {
        var store = new InMemoryUsageStore();
        store.Records.Add(UsageAt(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), usd));
        return store;
    }

    /// <summary>用量紀錄來源不同的首頁（<see cref="SettingsHost"/> 的首頁沒有來源）；同步派送。</summary>
    private static HomeViewModel HomeWith(SettingsHost host, IUsageStore usage) => new(host.Catalog, host.Status, new SyncUiDispatcher(), host.Store,
        host.McpSource, host.Tester, host.Clock, new MemoryOptions(), usage);

    /// <summary>和 <see cref="SettingsHost"/> 相同的分類 ViewModel，只把首頁換成 <paramref name="home"/>。</summary>
    private static SettingsViewModel SettingsWith(SettingsHost host, HomeViewModel home) => new(host.Connections, host.Models, host.ImageGeneration,
        host.Persona, host.Memory, host.Tools, host.McpServers, host.General, host.Sprite, host.Speech, host.Network, host.Browser,
        host.Reminders, host.Privacy, home, host.Usage, host.Catalog, host.Confirm, host.WindowService);

    [AvaloniaFact]
    public async Task NoBudget_ShowsTheAmount_AndHidesTheBudgetLineAndBars()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        using var home = HomeWith(host, SpentThisMonth(10m));
        var settings = SettingsWith(host, home);
        await home.LoadActivityAsync();
        var window = Host(settings);

        Assert.Equal("NT$320", BigNumber(window).Text);   // 10 美元 × 32
        Assert.True(BigNumber(window).IsEffectivelyVisible);
        Assert.False(TokensUnit(window).IsEffectivelyVisible);
        Assert.Equal(3, Bars(window).Count);
        Assert.All(Bars(window), bar => Assert.False(bar.IsEffectivelyVisible));
        window.Close();
    }

    /// <summary>預算 25 美元（NT$800）。進度條只顯示對應等級的那一條，顏色照用量頁的規則。</summary>
    [AvaloniaTheory]
    [InlineData(10, 0, 40)]
    [InlineData(20, 1, 80)]
    [InlineData(30, 2, 120)]
    public async Task WithBudget_ShowsTheBudgetLine_AndOnlyTheBarOfTheLevel(int spentUsd, int level, int percent)
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        await host.Catalog.SetUsageAsync(new UsageSettings(25m));
        using var home = HomeWith(host, SpentThisMonth(spentUsd));
        var settings = SettingsWith(host, home);
        await home.LoadActivityAsync();
        var window = Host(settings);

        var line = Card(window).GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Format(Strings.UsagePage_BudgetFormat, "NT$800", percent));
        Assert.True(line.IsEffectivelyVisible);

        var bars = Bars(window);
        Assert.Equal(3, bars.Count);
        for (var i = 0; i < bars.Count; i++)
            Assert.Equal(i == level, bars[i].IsEffectivelyVisible);

        var visible = bars[level];
        Assert.Equal(Math.Min(percent / 100.0, 1), visible.Value, 6);
        Assert.True(Application.Current!.TryGetResource(BarBrushKeys[level], window.ActualThemeVariant, out var expected));
        Assert.Equal(((ISolidColorBrush)expected!).Color, ((ISolidColorBrush)visible.Foreground!).Color);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ManageButton_IsTheUsageEntry_UsingTheSameCommandAsTheToolsCard()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        using var home = HomeWith(host, SpentThisMonth(10m));
        var settings = SettingsWith(host, home);
        await home.LoadActivityAsync();
        var window = Host(settings);

        var manage = Card(window).GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == Strings.Home_Manage);
        Assert.Equal("Usage", manage.CommandParameter);
        Assert.Same(settings.GoToCommand, manage.Command);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutAUsageStore_KeepsTheTokenNumbersAndTheUnit()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        await host.Catalog.SetUsageAsync(new UsageSettings(25m));
        var window = Host(host.Settings);   // 首頁附加到視窗時載入；沒有用量紀錄來源，預算不顯示

        Assert.True(TokensUnit(window).IsEffectivelyVisible);
        Assert.All(Bars(window), bar => Assert.False(bar.IsEffectivelyVisible));
        window.Close();
    }

    [AvaloniaFact]
    public async Task WhenTheUsageQueryFails_ShowsTheUnavailableLine()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        using var home = HomeWith(host, new FailingUsageStore());
        var settings = SettingsWith(host, home);
        await home.LoadActivityAsync();
        var window = Host(settings);

        Assert.False(BigNumber(window).IsEffectivelyVisible);
        var unavailable = Card(window).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.Home_UsageUnavailable);
        Assert.True(unavailable.IsEffectivelyVisible);
        window.Close();
    }

    /// <summary>有預算、接近上限（警告等級）的卡片，在五套主題 × 淺深色下，文字對比都 ≥ 4.5:1。</summary>
    [AvaloniaFact]
    public async Task UsageCard_HasTextContrast_InFivePalettesAndBothVariants()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        await host.Catalog.SetUsageAsync(new UsageSettings(25m));
        using var home = HomeWith(host, SpentThisMonth(24m));
        var settings = SettingsWith(host, home);
        await home.LoadActivityAsync();
        var window = Host(settings);
        var failures = new List<string>();
        try
        {
            foreach (var palette in ThemePalettes.All)
            {
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    ThemeService.ApplyPalette(Application.Current!.Resources.MergedDictionaries, palette);
                    window.RequestedThemeVariant = variant;
                    Relayout(window);

                    var samples = RenderedContrast.Measure(Card(window), out var problems);
                    Assert.True(samples.Count >= 3, $"{palette} {variant} 只量到 {samples.Count} 段文字");
                    failures.AddRange(problems.Select(p => $"{palette} {variant}：{p}"));
                    failures.AddRange(samples.Where(s => s.Ratio < Wcag.TextMinimum).Select(s => $"{palette} {variant}：{s}"));
                }
            }
        }
        finally
        {
            SettingsRig.ResetTheme();
            window.Close();
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(40)));
    }

    /// <summary>所有查詢都丟例外的用量紀錄來源。</summary>
    private sealed class FailingUsageStore : IUsageStore
    {
        public Task AddAsync(UsageRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UsageRecord>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<UsageRecord>>(new IOException("測試用的讀取失敗"));

        public Task<ConversationCost> GetConversationCostAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationCostItem>> ListTopConversationsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<decimal> SumCostAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
