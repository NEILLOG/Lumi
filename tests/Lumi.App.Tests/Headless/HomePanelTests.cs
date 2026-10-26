using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;

namespace Lumi.App.Tests.Headless;

/// <summary>M20 20G：設定首頁（<c>HomePanel</c>）的狀態卡片——6 張卡片、每張有無障礙名稱、點了切到對應分類、沒有模型時的提示。</summary>
public class HomePanelTests
{
    private static IEnumerable<Button> HomeCards(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("home-card"));

    /// <summary>獨立視窗：設定視窗提供的 muted 文字樣式，這裡用同一份共用樣式。</summary>
    private static Window Host(SettingsHost host, double width = 1040)
    {
        var window = new Window { Width = width, Height = 900, Content = new HomePanel { DataContext = host.Settings } };
        window.Styles.Add(new StyleInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/ChatStyles.axaml") });
        window.Classes.Add("wide");
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static Button CardNamed(Visual root, string name) =>
        HomeCards(root).Single(b => AutomationProperties.GetName(b) == name);

    [AvaloniaFact]
    public void Panel_ShowsSixCards()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);

        Assert.Equal(6, HomeCards(window).Count());
    }

    [AvaloniaFact]
    public void EveryCard_HasAnAccessibilityName()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);

        foreach (var button in HomeCards(window))
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
    }

    [AvaloniaFact]
    public void MemoryCard_SwitchesToTheMemoryCategory()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);
        var memory = CardNamed(window, Strings.Settings_CategoryMemory);

        memory.Command!.Execute(memory.CommandParameter);

        Assert.True(host.Settings.ShowMemory);
        Assert.False(host.Settings.ShowConnectionsAndModels);
    }

    [AvaloniaFact]
    public void ConnectionsCard_SwitchesToConnectionsAndTheConnectionsTab()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);
        var connections = CardNamed(window, Strings.Settings_CategoryConnections);

        connections.Command!.Execute(connections.CommandParameter);

        Assert.True(host.Settings.ShowConnectionsAndModels);
        Assert.True(host.Settings.ShowConnectionsTab);
    }

    [AvaloniaFact]
    public async Task WithoutAModel_TheConnectionsCardAsksForOne()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        // 沒有設定檔時預設會有一個模型，這裡刪掉，才看得到「尚未設定模型」
        foreach (var model in host.Catalog.Current.Models)
            await host.Catalog.DeleteModelAsync(model.Id);
        await host.Home.RefreshAsync();
        var window = Host(host);
        var connections = CardNamed(window, Strings.Settings_CategoryConnections);

        var texts = connections.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(Strings.Home_NoModel, texts);
        Assert.Contains(Strings.Home_Attention, texts);
    }
}
