using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 「AI 連線」頁詳細區的連線表單。DataContext 是 <see cref="ViewModels.ConnectionsViewModel"/>。
/// code-behind 只放視覺行為：把焦點放到金鑰輸入框。
/// </summary>
public partial class ConnectionsPanel : UserControl
{
    public ConnectionsPanel()
    {
        InitializeComponent();
    }

    /// <summary>金鑰輸入框（缺金鑰流程要把焦點放進去）。</summary>
    public void FocusKeyBox() => KeyBox.Focus();
}
