using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.App.Views.Responsive;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M21 21F：設定首頁（<c>HomePanel</c>）——各區塊的名稱、待處理與無待處理、記憶卡沒有開關、
/// 720／900／1040 寬的版面（1 欄、2 欄、3 欄）、側欄橘點、淺色與深色各 render 一次。
/// </summary>
public class HomePanelTests
{
    /// <summary>獨立視窗：掛上 Track（寬度 class 與 stacked），內容是首頁。設定視窗也用同樣的 class 規則。</summary>
    private static Window Host(SettingsHost host, double width = 1040)
    {
        var window = new Window { Width = width, Height = 900, Content = new HomePanel { DataContext = host.Settings } };
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

    /// <summary>有模型、有金鑰、連線已測試、語音端點已填：沒有任何待處理項目。</summary>
    private static async Task UseAllGoodAsync(SettingsHost host)
    {
        var connection = new ConnectionSettings("conn1", "測試連線", ProviderFormat.Anthropic, "https://api.example.test");
        await host.Catalog.SaveConnectionAsync(connection, new SecretValue("sk-test"));
        await host.Catalog.SaveModelAsync(new ModelSettings("model1", "conn1", "claude-test-1", "測試模型", 4096, 200000, true));
        await host.Catalog.SetWorkbenchModelAsync("model1");
        host.Status.Mark("conn1", ConnectionStatus.Ok);
        await host.Catalog.SetSpeechAsync(new SpeechSettings("https://speech.example.test/v1"), null);
        await host.Home.RefreshAsync();
    }

    private static async Task RemoveAllModelsAsync(SettingsHost host)
    {
        foreach (var model in host.Catalog.Current.Models)
            await host.Catalog.DeleteModelAsync(model.Id);
        await host.Home.RefreshAsync();
    }

    private static readonly string[] BlockNames =
        ["HomePendingList", "HomeModelCard", "HomeMemoryCard", "HomeUsageCard", "HomeToolsCard", "HomeRemindersCard", "HomeRecentActions"];

    [AvaloniaFact]
    public void EveryBlock_HasItsName()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);

        foreach (var name in BlockNames)
            Assert.NotNull(window.GetVisualDescendants().OfType<Control>().SingleOrDefault(c => c.Name == name));
    }

    [AvaloniaFact]
    public async Task NoPending_ShowsTheAllGoodLine()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        await UseAllGoodAsync(host);
        var window = Host(host);

        Assert.False(host.Settings.Home.HasPending);
        Assert.False(Named<StackPanel>(window, "HomePendingSection").IsVisible);
        var line = Named<TextBlock>(window, "HomeNoPendingText");
        Assert.True(line.IsEffectivelyVisible);
        Assert.Equal(Strings.Home_AllGood, line.Text);
    }

    [AvaloniaFact]
    public async Task PendingButtons_HaveACommandAndAnAccessibleName()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        await RemoveAllModelsAsync(host);   // 沒有模型：待處理第一條「前往設定」
        var window = Host(host);

        Assert.True(host.Settings.Home.HasPending);
        Assert.True(Named<StackPanel>(window, "HomePendingSection").IsVisible);
        Assert.False(Named<StackPanel>(window, "HomeNoPendingRow").IsVisible);
        var buttons = Named<ItemsControl>(window, "HomePendingList").GetVisualDescendants().OfType<Button>().ToList();
        Assert.NotEmpty(buttons);
        foreach (var button in buttons)
        {
            Assert.NotNull(button.Command);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
        }
    }

    [AvaloniaFact]
    public async Task MemoryCard_HasNoToggleOrCheckBox()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);

        var memory = Named<Border>(window, "HomeMemoryCard");
        Assert.Empty(memory.GetVisualDescendants().OfType<ToggleSwitch>());
        Assert.Empty(memory.GetVisualDescendants().OfType<CheckBox>());
    }

    [AvaloniaFact]
    public void Width720_IsOneColumn_WithoutHorizontalScroll()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host, 720);

        Assert.Contains("stacked", window.Classes);
        var cards = new[] { "HomeModelCard", "HomeMemoryCard", "HomeUsageCard", "HomeToolsCard", "HomeRemindersCard" }
            .Select(n => Named<Border>(window, n).Bounds).ToList();
        // 一欄：每張卡的 X 與寬度相同，而且從上往下排
        Assert.All(cards, b => Assert.Equal(cards[0].X, b.X, 0.5));
        Assert.All(cards, b => Assert.Equal(cards[0].Width, b.Width, 0.5));
        for (var i = 1; i < cards.Count; i++)
            Assert.True(cards[i].Y > cards[i - 1].Y, $"第 {i + 1} 張應該在第 {i} 張下面");

        // 沒有水平捲動：水平捲軸停用，卡片都在格子寬度之內
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        var grid = Named<Grid>(window, "HomeCardGrid");
        Assert.All(cards, b => Assert.True(b.Right <= grid.Bounds.Width + 0.5));
    }

    [AvaloniaFact]
    public void Width900_IsTwoColumns()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host, 900);

        Assert.Contains("compact", window.Classes);
        var model = Named<Border>(window, "HomeModelCard").Bounds;
        var memory = Named<Border>(window, "HomeMemoryCard").Bounds;
        var usage = Named<Border>(window, "HomeUsageCard").Bounds;
        Assert.Equal(model.Y, memory.Y, 0.5);
        Assert.True(usage.Y > model.Y);
    }

    [AvaloniaFact]
    public void Width1040_FirstRowHasThreeCardsSideBySide_ToolsAndRemindersBelow()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host, 1040);

        Assert.Contains("wide", window.Classes);
        var model = Named<Border>(window, "HomeModelCard").Bounds;
        var memory = Named<Border>(window, "HomeMemoryCard").Bounds;
        var usage = Named<Border>(window, "HomeUsageCard").Bounds;
        var tools = Named<Border>(window, "HomeToolsCard").Bounds;
        var reminders = Named<Border>(window, "HomeRemindersCard").Bounds;

        Assert.Equal(model.Y, memory.Y, 0.5);
        Assert.Equal(model.Y, usage.Y, 0.5);
        Assert.True(model.X < memory.X && memory.X < usage.X);
        Assert.Equal(tools.Y, reminders.Y, 0.5);
        Assert.True(tools.Y > model.Y);
        Assert.True(tools.X < reminders.X);
    }

    [AvaloniaFact]
    public async Task SidebarAttentionDots_FollowTheAttentionFlags()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(host.Settings);
        window.Show();
        window.UpdateLayout();

        // 預設：沒有語音端點（橘點）、沒有模型（連線橘點）
        await RemoveAllModelsAsync(host);
        Assert.True(host.Settings.Home.AttentionSpeech);
        Assert.True(host.Settings.Home.AttentionConnections);
        Relayout(window);
        Assert.True(Named<Ellipse>(window, "ConnectionsAttentionDot").IsVisible);
        Assert.True(Named<Ellipse>(window, "SpeechAttentionDot").IsVisible);
        Assert.Equal(Strings.Home_Attention, AutomationProperties.GetHelpText(Named<Ellipse>(window, "ConnectionsAttentionDot")));

        // 全部就緒：橘點消失
        await UseAllGoodAsync(host);
        Assert.False(host.Settings.Home.AttentionSpeech);
        Assert.False(host.Settings.Home.AttentionConnections);
        Relayout(window);
        Assert.False(Named<Ellipse>(window, "ConnectionsAttentionDot").IsVisible);
        Assert.False(Named<Ellipse>(window, "SpeechAttentionDot").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panel_RendersInLightAndDarkThemes()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        await UseAllGoodAsync(host);
        var window = Host(host, 1040);

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = variant;
            Relayout(window);
            Assert.True(Named<Border>(window, "HomeModelCard").IsEffectivelyVisible);
            Assert.True(Named<Border>(window, "HomeRecentActions").IsEffectivelyVisible);
        }
        window.Close();
    }
}
