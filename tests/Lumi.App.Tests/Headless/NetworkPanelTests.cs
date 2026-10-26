using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M17 17G：設定 › 網路頁的版面與欄位顯示規則。面板放在真正的設定視窗裡（分類 7），
/// 單獨放進空白視窗會缺少設定視窗提供的樣式。
/// </summary>
public class NetworkPanelTests
{
    private static (SettingsHost Host, SettingsWindow Window) Open()
    {
        var host = new SettingsHost();
        var window = new SettingsWindow(host.Settings);
        window.Show();
        host.Settings.CategoryIndex = 9;
        window.UpdateLayout();
        return (host, window);
    }

    [AvaloniaFact]
    public void Category_shows_the_network_page()
    {
        var (host, window) = Open();
        using var _ = host;

        Assert.True(window.FindByName<Button>("NetworkSaveButton").IsEffectivelyVisible);
        Assert.True(window.FindByName<RadioButton>("NetworkModeSystem").IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void Pac_mode_shows_only_the_pac_field()
    {
        var (host, window) = Open();
        using var _ = host;

        host.Network.ModeIndex = 1;
        window.UpdateLayout();

        Assert.True(window.FindByName<FormField>("NetworkPacUrlField").IsEffectivelyVisible);
        Assert.False(window.FindByName<FormField>("NetworkManualProxyField").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Manual_mode_shows_the_proxy_and_bypass_fields()
    {
        var (host, window) = Open();
        using var _ = host;

        host.Network.ModeIndex = 2;
        window.UpdateLayout();

        Assert.True(window.FindByName<FormField>("NetworkManualProxyField").IsEffectivelyVisible);
        Assert.True(window.FindByName<FormField>("NetworkBypassField").IsEffectivelyVisible);
        Assert.False(window.FindByName<FormField>("NetworkPacUrlField").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void No_proxy_mode_hides_the_authentication_choices()
    {
        var (host, window) = Open();
        using var _ = host;
        Assert.True(window.FindByName<FormField>("NetworkAuthField").IsEffectivelyVisible);

        host.Network.ModeIndex = 3;
        window.UpdateLayout();

        Assert.False(window.FindByName<FormField>("NetworkAuthField").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Account_auth_shows_the_user_and_password_fields()
    {
        var (host, window) = Open();
        using var _ = host;
        Assert.False(window.FindByName<FormField>("NetworkProxyUserField").IsEffectivelyVisible);

        host.Network.AuthIndex = 2;
        window.UpdateLayout();

        Assert.True(window.FindByName<FormField>("NetworkProxyUserField").IsEffectivelyVisible);
        Assert.True(window.FindByName<FormField>("NetworkPasswordField").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Switching_radio_buttons_does_not_write_back_in_a_loop()
    {
        var (host, window) = Open();
        using var _ = host;
        var pac = window.FindByName<RadioButton>("NetworkModePac");
        var manual = window.FindByName<RadioButton>("NetworkModeManual");

        pac.IsChecked = true;
        window.UpdateLayout();
        Assert.Equal(1, host.Network.ModeIndex);

        manual.IsChecked = true;
        window.UpdateLayout();

        Assert.Equal(2, host.Network.ModeIndex);
        Assert.False(pac.IsChecked);
        Assert.True(manual.IsChecked);
        window.Close();
    }
}
