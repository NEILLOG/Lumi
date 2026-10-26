using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Browser;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M18 18I：設定 › 網路頁底部的「瀏覽器」區段。面板放在真正的設定視窗裡（分類 7），
/// 單獨放進空白視窗會缺少設定視窗提供的樣式。
/// </summary>
public class BrowserSettingsSectionTests
{
    private static (SettingsHost Host, SettingsWindow Window) Open(params InstalledBrowser[] installed)
    {
        var host = new SettingsHost();
        host.BrowserLocator.Installed.AddRange(installed);
        host.Browser.RefreshCommand.Execute(null);
        var window = new SettingsWindow(host.Settings);
        window.Show();
        host.Settings.CategoryIndex = 9;
        window.UpdateLayout();
        return (host, window);
    }

    /// <summary>設定是非同步寫入的（真的 <c>JsonModelCatalog</c> 要寫檔），最多等 5 秒。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
    }

    private static InstalledBrowser Edge => new(BrowserKind.Edge, "/apps/edge", "138.0.1");

    [AvaloniaFact]
    public void The_section_is_part_of_the_network_page()
    {
        var (host, window) = Open(Edge);
        using var _ = host;

        Assert.True(window.FindByName<RadioButton>("BrowserChoiceAuto").IsEffectivelyVisible);
        Assert.True(window.FindByName<CheckBox>("BrowserShowWindowBox").IsEffectivelyVisible);
        Assert.True(window.FindByName<Button>("BrowserClearButton").IsEffectivelyVisible);
        // 網路頁自己的儲存鈕還在
        Assert.True(window.FindByName<Button>("NetworkSaveButton").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_radio_buttons_does_not_write_back_in_a_loop()
    {
        var (host, window) = Open(Edge);
        using var _ = host;
        var edge = window.FindByName<RadioButton>("BrowserChoiceEdge");
        var chrome = window.FindByName<RadioButton>("BrowserChoiceChrome");

        edge.IsChecked = true;
        await WaitUntilAsync(() => host.Catalog.Current.Browser.Choice == BrowserChoice.Edge);
        window.UpdateLayout();
        Assert.Equal(BrowserChoice.Edge, host.Catalog.Current.Browser.Choice);

        chrome.IsChecked = true;
        await WaitUntilAsync(() => host.Catalog.Current.Browser.Choice == BrowserChoice.Chrome);
        window.UpdateLayout();

        Assert.Equal(BrowserChoice.Chrome, host.Catalog.Current.Browser.Choice);
        Assert.False(edge.IsChecked);
        Assert.True(chrome.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ticking_show_window_changes_the_setting_immediately()
    {
        var (host, window) = Open(Edge);
        using var _ = host;

        window.FindByName<CheckBox>("BrowserShowWindowBox").IsChecked = true;
        await WaitUntilAsync(() => host.Catalog.Current.Browser.ShowWindow);
        window.UpdateLayout();

        Assert.True(host.Catalog.Current.Browser.ShowWindow);
        Assert.False(host.Settings.HasUnsavedChanges);   // 立即生效，不算未儲存的變更
        window.Close();
    }

    [AvaloniaFact]
    public void No_browser_installed_shows_the_warning_text()
    {
        var (host, window) = Open();
        using var _ = host;

        var text = window.FindByName<TextBlock>("BrowserDetectionText");

        Assert.True(text.IsEffectivelyVisible);
        Assert.Contains("找不到", text.Text);
        window.Close();
    }
}
