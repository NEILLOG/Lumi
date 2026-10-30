using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Models;
using Lumi.Core.Usage;
using TextBlock = Avalonia.Controls.TextBlock;
using Button = Avalonia.Controls.Button;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// 設定 › 用量與費用（<c>UsagePanel</c>）的畫面：本月卡片的三種狀態、預算原地展開、提示段落空時不顯示、
/// 每天長條的提示文字，以及五套主題淺深色下所有可見文字的對比 ≥ 4.5:1（仿照 <c>SettingsContrastTests</c>）。
/// </summary>
public class UsagePanelTests
{
    /// <summary>獨立視窗：內容是用量頁，掛上聊天樣式（<c>TextBlock.muted</c>）。背景由 <see cref="ApplyChrome"/> 依主題設定。</summary>
    private static Window Host(UsageSettingsViewModel vm, double width = 720)
    {
        var window = new Window { Width = width, Height = 1000, Content = new UsagePanel { DataContext = vm } };
        window.Styles.Add(new StyleInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/ChatStyles.axaml") });
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

    /// <summary>視窗底色等於目前主題的 Chrome 色（渲染對比量測需要不透明的底）。</summary>
    private static void ApplyChrome(Window window)
    {
        if (Application.Current!.TryGetResource("LumiChromeBrush", window.ActualThemeVariant, out var chrome))
            window.Background = chrome as IBrush;
    }

    private static bool Shown(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    private static bool ButtonShown(Visual root, string content) =>
        root.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content as string == content);

    private static Button ButtonNamed(Visual root, string content) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == content);

    private static void Click(Button button)
    {
        Assert.NotNull(button.Command);
        Assert.True(button.Command!.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
    }

    /// <summary>有預算（NT$800）、本月 NT$488（61%）、10 月中旬起每天有紀錄（達到預測門檻）、一則可開啟的對話、一條被靜音的提示。</summary>
    private static async Task<(UsageSettingsViewModel Vm, FakeModelCatalog Catalog)> RichAsync(decimal? budgetUsd = 25m, decimal total = 15.25m,
        Func<string, Task>? openConversation = null)
    {
        var catalog = new FakeModelCatalog();
        catalog.Current = catalog.Current with
        {
            Usage = new UsageSettings(budgetUsd, new[] { UsageHintIds.Images }),
        };
        var store = new InMemoryUsageStore();
        var conversations = new InMemoryConversationStore();
        var hints = new InMemoryUsageHintLog();
        var conversation = await conversations.CreateAsync("重構設定頁");

        // 10/23–10/29 每天一筆；前 6 天取兩位小數，最後一天補足，合計剛好是 total
        var each = Math.Floor(total / 7 * 100m) / 100m;
        for (var day = 23; day <= 29; day++)
        {
            var amount = day == 29 ? total - each * 6 : each;
            store.Records.Add(UsageSamples.Record(UsageSamples.Day(day), amount, conversation.Id));
        }

        hints.Entries.Add(UsageSamples.Hint(UsageHintIds.LongConversation, conversation.Id, UsageSamples.Day(25)));
        hints.Entries.Add(UsageSamples.Hint(UsageHintIds.Images, conversation.Id, UsageSamples.Day(26)));

        var top = new[] { new ConversationCostItem(conversation.Id, "重構設定頁", UsageSamples.Day(29), 12, total, false) };
        var vm = new UsageSettingsViewModel(new TopConversationsStore(store, top), hints, catalog, conversations, new FakeSavePicker(),
            UsageSamples.October29(), openConversation);
        await vm.LoadAsync();
        return (vm, catalog);
    }

    // ===== 本月卡片的三種狀態 =====

    [AvaloniaFact]
    public async Task EmptyMonth_ShowsOnlyTheZeroAmountAndTheEmptyNote()
    {
        var vm = new UsageSettingsViewModel(new InMemoryUsageStore(), new InMemoryUsageHintLog(), new FakeModelCatalog(),
            new InMemoryConversationStore(), new FakeSavePicker(), UsageSamples.October29());
        await vm.LoadAsync();
        var window = Host(vm);

        Assert.True(Shown(window, "NT$0"));
        Assert.True(Shown(window, Strings.UsagePage_Empty));
        Assert.True(ButtonShown(window, Strings.UsagePage_SetBudget));    // 還沒有用量的人也要能先設定預算
        Assert.False(Shown(window, Strings.UsagePage_DailyTitle));
        Assert.False(Shown(window, Strings.UsagePage_TopTitle));
        Assert.False(Shown(window, Strings.UsagePage_HintsTitle));
        Assert.False(window.GetVisualDescendants().OfType<DailyCostBars>().Single().IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithBudget_ShowsAmountPercentEditButtonAndSections()
    {
        var (vm, _) = await RichAsync();
        var window = Host(vm);

        Assert.True(Shown(window, "NT$488"));
        Assert.True(Shown(window, "每月預算 NT$800 · 已用 61%"));
        Assert.True(ButtonShown(window, Strings.UsagePage_EditBudget));
        Assert.False(ButtonShown(window, Strings.UsagePage_SetBudget));
        Assert.True(Shown(window, Strings.UsagePage_DailyTitle));
        Assert.True(window.GetVisualDescendants().OfType<DailyCostBars>().Single().IsEffectivelyVisible);
        Assert.True(Shown(window, "重構設定頁"));
        Assert.True(Shown(window, Strings.UsagePage_HintsTitle));
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutBudget_ShowsOnlyTheSetLink_AndItOpensTheEditorInPlace()
    {
        var (vm, _) = await RichAsync(budgetUsd: null);
        var window = Host(vm);

        Assert.False(ButtonShown(window, Strings.UsagePage_EditBudget));
        Assert.True(ButtonShown(window, Strings.UsagePage_SetBudget));
        Assert.False(Shown(window, Strings.UsagePage_BudgetNote));

        Click(ButtonNamed(window, Strings.UsagePage_SetBudget));
        Relayout(window);

        Assert.True(Shown(window, Strings.UsagePage_BudgetNote));
        Assert.True(ButtonShown(window, Strings.UsagePage_Save));
        Assert.True(ButtonShown(window, Strings.UsagePage_Cancel));
        Assert.True(ButtonShown(window, Strings.UsagePage_NoBudget));
        Assert.False(ButtonShown(window, Strings.UsagePage_SetBudget));
        window.Close();
    }

    [AvaloniaFact]
    public async Task EditingTheBudget_ShowsTheInputAndCancelReturnsToTheSummary()
    {
        var (vm, _) = await RichAsync();
        var window = Host(vm);

        Click(ButtonNamed(window, Strings.UsagePage_EditBudget));
        Relayout(window);

        var input = window.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) == Strings.UsagePage_BudgetInputLabel);
        Assert.Equal("800", input.Text);
        Assert.False(ButtonShown(window, Strings.UsagePage_EditBudget));

        Click(ButtonNamed(window, Strings.UsagePage_Cancel));
        Relayout(window);

        Assert.True(ButtonShown(window, Strings.UsagePage_EditBudget));
        Assert.False(ButtonShown(window, Strings.UsagePage_Save));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Warning_TintsTheProgressBarWithTheWarningClass()
    {
        var (vm, _) = await RichAsync(budgetUsd: 20m, total: 17m);   // US$17 ÷ US$20 = 85%（NT$544 ÷ NT$640）
        var window = Host(vm);

        var bar = window.GetVisualDescendants().OfType<ProgressBar>().Single(p => p.Classes.Contains("usage-bar"));
        Assert.Contains("warning", bar.Classes);
        Assert.DoesNotContain("danger", bar.Classes);
        window.Close();
    }

    // ===== 提示段落 =====

    [AvaloniaFact]
    public async Task Hints_ShowMutedMarkAndExpandToTheConversation()
    {
        var (vm, _) = await RichAsync();
        var window = Host(vm);

        // Images 被靜音：說明後多一行「已關閉提醒 · 重新開啟」
        Assert.True(Shown(window, Strings.UsagePage_HintMuted));
        Assert.True(ButtonShown(window, Strings.UsagePage_HintUnmute));
        // 長對話一直聊下去：有可列的對話，展開前看不到對話標題列（標題本身在卡片的花最多清單裡也有一次）
        var expanded = vm.Hints.Single(h => h.Rule == UsageHintIds.LongConversation);
        Assert.False(expanded.IsExpanded);

        vm.ToggleHintCommand.Execute(UsageHintIds.LongConversation);
        Relayout(window);

        Assert.True(expanded.IsExpanded);
        var titles = window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible && t.Text == "重構設定頁");
        Assert.True(titles >= 2);   // 花最多的對話 ＋ 展開後的對話清單
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hints_SectionIsHiddenWhenNothingHappenedThisMonth()
    {
        var vm = new UsageSettingsViewModel(new InMemoryUsageStore(), new InMemoryUsageHintLog(), new FakeModelCatalog(),
            new InMemoryConversationStore(), new FakeSavePicker(), UsageSamples.October29());
        await vm.LoadAsync();
        var window = Host(vm);

        Assert.False(Shown(window, Strings.UsagePage_HintsTitle));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), b => b.IsEffectivelyVisible && b.Classes.Contains("list-box"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TopConversation_Click_OpensThatConversation()
    {
        var opened = new List<string>();
        var (vm, _) = await RichAsync(openConversation: id => { opened.Add(id); return Task.CompletedTask; });
        var window = Host(vm);

        var conversationId = vm.TopConversations.Single().ConversationId;
        var row = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && Equals(b.CommandParameter, conversationId) && b.Classes.Contains("list-item"));
        Click(row);

        Assert.Equal(new[] { conversationId }, opened);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HintRow_Click_ExpandsItsConversations_AndUnmuteWritesSettings()
    {
        var (vm, catalog) = await RichAsync();
        var window = Host(vm);

        // 展開：點規則列，才會看到對話清單裡的對話按鈕
        var toggle = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && Equals(b.CommandParameter, UsageHintIds.LongConversation) && b.Classes.Contains("list-item"));
        Click(toggle);
        Relayout(window);
        Assert.True(vm.Hints.Single(h => h.Rule == UsageHintIds.LongConversation).IsExpanded);
        // 展開後，對話清單裡的對話按鈕可見（花最多的對話清單裡也有一個同一則對話）
        var conversationId = vm.Hints.Single(h => h.Rule == UsageHintIds.LongConversation).Conversations.Single().ConversationId;
        Assert.Equal(2, window.GetVisualDescendants().OfType<Button>()
            .Count(b => b.IsEffectivelyVisible && Equals(b.CommandParameter, conversationId)));

        // 重新開啟：寫回設定（被靜音的 Images 那一條變成不靜音）
        Click(ButtonNamed(window, Strings.UsagePage_HintUnmute));
        Relayout(window);
        Assert.DoesNotContain(UsageHintIds.Images, catalog.UsageSaves.Last().Muted);
        Assert.False(vm.Hints.Single(h => h.Rule == UsageHintIds.Images).IsMuted);
        Assert.False(Shown(window, Strings.UsagePage_HintMuted));
        window.Close();
    }

    // ===== 每天長條的提示 =====

    [AvaloniaFact]
    public async Task DailyBars_TooltipNamesTheDayUnderThePointer()
    {
        var (vm, _) = await RichAsync();
        var window = Host(vm);
        var bars = window.GetVisualDescendants().OfType<DailyCostBars>().Single();
        Assert.Equal(31, bars.Items!.Count);

        // 10/29（今天，第 29 根）的中心
        var x = bars.Bounds.Width * (28 + 0.5) / 31;
        var point = bars.TranslatePoint(new Point(x, bars.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        Relayout(window);

        var tip = ToolTip.GetTip(bars) as string;
        Assert.NotNull(tip);
        Assert.StartsWith("10/29 · ", tip);

        // 未來的日子（10/31）沒有提示
        var futureX = bars.Bounds.Width * (30 + 0.5) / 31;
        window.MouseMove(bars.TranslatePoint(new Point(futureX, bars.Bounds.Height / 2), window)!.Value);
        Relayout(window);
        Assert.Null(ToolTip.GetTip(bars));
        window.Close();
    }

    // ===== 對比 =====

    [AvaloniaFact]
    public async Task EveryVisibleText_MeetsTheTextContrast_InFivePalettesAndBothVariants()
    {
        var (vm, _) = await RichAsync();
        vm.ToggleHintCommand.Execute(UsageHintIds.LongConversation);
        var window = Host(vm);
        var failures = new List<string>();
        var accents = new HashSet<Color>();
        try
        {
            foreach (var palette in ThemePalettes.All)
            {
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    ThemeService.ApplyPalette(Application.Current!.Resources.MergedDictionaries, palette);
                    window.RequestedThemeVariant = variant;
                    Relayout(window);
                    ApplyChrome(window);
                    Relayout(window);

                    Assert.True(Application.Current.TryGetResource("LumiAccentBrush", window.ActualThemeVariant, out var accent));
                    if (variant == ThemeVariant.Light)
                        accents.Add(((ISolidColorBrush)accent!).Color);

                    // 一般狀態，以及預算編輯展開的狀態
                    foreach (var state in new[] { "default", "editing" })
                    {
                        if (state == "editing")
                        {
                            vm.EditBudgetCommand.Execute(null);
                            Relayout(window);
                        }

                        var samples = RenderedContrast.Measure(window, out var problems);
                        Assert.True(samples.Count > 20, $"{palette} {variant} {state} 只量到 {samples.Count} 段文字");
                        foreach (var problem in problems)
                            failures.Add($"{palette} {variant} {state}：{problem}");
                        foreach (var sample in samples.Where(s => s.Ratio < Wcag.TextMinimum))
                            failures.Add($"{palette} {variant} {state}：{sample}");

                        if (state == "editing")
                        {
                            vm.CancelBudgetCommand.Execute(null);
                            Relayout(window);
                        }
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
        window.Close();
    }
}
