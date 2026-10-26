using Avalonia;
using Avalonia.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 設定視窗「資料與隱私」的內容（M20 20C）。頁首在 <see cref="SettingsWindow"/>；行為在 <see cref="DataPrivacyViewModel"/>。
/// 進入畫面時重新計算各位置的大小與存在狀態（大小可能隨使用而改變，所以不在建構時算）。
/// </summary>
public partial class DataPrivacyPanel : UserControl
{
    public DataPrivacyPanel()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = (DataContext as DataPrivacyViewModel)?.RefreshSizesAsync();
    }
}
