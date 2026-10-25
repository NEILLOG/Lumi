using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定視窗「工具」的內容（M15 15D）。頁首在 <see cref="SettingsWindow"/>；這裡的行為全部在 <c>ToolsViewModel</c>，
/// code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class ToolsPanel : UserControl
{
    public ToolsPanel()
    {
        InitializeComponent();
    }
}
