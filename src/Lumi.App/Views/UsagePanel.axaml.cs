using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定 › 用量與費用（plan/23-usage-cost.md 第 6 節）的內容。頁首與分類由 <c>SettingsWindow</c> 提供；
/// 行為全部在 <c>UsageSettingsViewModel</c>，code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class UsagePanel : UserControl
{
    public UsagePanel()
    {
        InitializeComponent();
    }
}
