using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定視窗「網路」底部的「瀏覽器」區段（M18 D3）。行為全部在 <c>BrowserSettingsViewModel</c>，
/// code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class BrowserSettingsSection : UserControl
{
    public BrowserSettingsSection()
    {
        InitializeComponent();
    }
}
