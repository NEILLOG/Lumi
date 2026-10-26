using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定首頁（M20 20D）：常用設定的連結。行為全部在 <c>SettingsViewModel.GoToCommand</c>，
/// code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class HomePanel : UserControl
{
    public HomePanel()
    {
        InitializeComponent();
    }
}
