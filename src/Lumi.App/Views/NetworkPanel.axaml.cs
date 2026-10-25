using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定視窗「網路」的內容（M17 17G）。頁首與分類在 <see cref="SettingsWindow"/>；這裡的行為全部在 <c>NetworkViewModel</c>，
/// code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class NetworkPanel : UserControl
{
    public NetworkPanel()
    {
        InitializeComponent();
    }
}
