using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 「AI 連線」頁的連線分段：清單卡片＋詳細表單。DataContext 是 <see cref="ConnectionsViewModel"/>。
/// code-behind 只放視覺行為：把焦點放到金鑰輸入框；「進階」收合時若有錯誤或警告就自動展開。
/// </summary>
public partial class ConnectionsPanel : UserControl
{
    public ConnectionsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionsViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(ConnectionsViewModel.BaseUrlError) or nameof(ConnectionsViewModel.BaseUrlWarning))
                        Dispatcher.UIThread.Post(() => ExpandAdvancedWhenNeeded(vm), DispatcherPriority.Loaded);   // 載入表單時欄位一個一個設定，等整個表單設好再看有沒有錯誤
                };
                ExpandAdvancedWhenNeeded(vm);
            }
        };
    }

    /// <summary>金鑰輸入框（缺金鑰流程要把焦點放進去）。</summary>
    public void FocusKeyBox() => KeyBox.Focus();

    private void ExpandAdvancedWhenNeeded(ConnectionsViewModel vm)
    {
        if (vm.HasEditor && (vm.BaseUrlError is not null || vm.BaseUrlWarning is not null))
            AdvancedExpander.IsExpanded = true;
    }
}
