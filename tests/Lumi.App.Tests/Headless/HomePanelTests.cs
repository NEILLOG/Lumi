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

/// <summary>M20 20D：設定首頁（<c>HomePanel</c>）的畫面與連結——11 個常用設定連結、點了切到對應分類、每個連結有無障礙名稱。</summary>
public class HomePanelTests
{
    private static IEnumerable<Button> HomeLinks(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("home-link"));

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

    private static Button LinkNamed(Visual root, string name) =>
        HomeLinks(root).Single(b => AutomationProperties.GetName(b) == name);

    [AvaloniaFact]
    public void Panel_ShowsElevenLinks()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);

        Assert.Equal(11, HomeLinks(window).Count());
    }

    [AvaloniaFact]
    public void EveryLink_HasAnAccessibilityName()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);

        foreach (var button in HomeLinks(window))
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
    }

    [AvaloniaFact]
    public void MemoryLink_SwitchesToTheMemoryCategory()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);
        var memory = LinkNamed(window, Strings.Settings_CategoryMemory);

        memory.Command!.Execute(memory.CommandParameter);

        Assert.True(host.Settings.ShowMemory);
        Assert.False(host.Settings.ShowConnectionsAndModels);
    }

    [AvaloniaFact]
    public void ModelsLink_SwitchesToConnectionsAndTheModelsTab()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);
        var models = LinkNamed(window, Strings.Home_LinkModels);

        models.Command!.Execute(models.CommandParameter);

        Assert.True(host.Settings.ShowConnectionsAndModels);
        Assert.True(host.Settings.ShowModelsTab);
    }

    [AvaloniaFact]
    public void ToolsLink_SwitchesToTheToolsCategory()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Host(host);
        var tools = LinkNamed(window, Strings.Tools_MenuTitle);

        tools.Command!.Execute(tools.CommandParameter);

        Assert.True(host.Settings.ShowTools);
        Assert.True(host.Settings.ShowToolsListTab);
    }
}
