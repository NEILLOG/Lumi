using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定頁「提醒排程」的內容（M20 20B）。行為全部在 <c>RemindersViewModel</c>，code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class RemindersPanel : UserControl
{
    public RemindersPanel()
    {
        InitializeComponent();
    }
}
