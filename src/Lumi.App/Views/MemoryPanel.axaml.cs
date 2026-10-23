using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定視窗「記憶」的內容（M14 14D）。頁首與「新增記憶」在 <see cref="SettingsWindow"/>；這裡的行為全部在 <c>MemoryViewModel</c>，
/// code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class MemoryPanel : UserControl
{
    public MemoryPanel()
    {
        InitializeComponent();
    }
}
