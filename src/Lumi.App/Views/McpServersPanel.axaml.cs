using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 設定頁「工具」分段的「MCP 伺服器」：清單卡片＋詳細表單或匯入畫面。DataContext 是 <see cref="McpServersViewModel"/>。
/// code-behind 只放視覺行為：「進階」收合時若有代號或逾時的錯誤就自動展開。
/// </summary>
public partial class McpServersPanel : UserControl
{
    public McpServersPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is McpServersViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(McpServersViewModel.AdvancedHasError))
                        Dispatcher.UIThread.Post(() => ExpandAdvancedWhenNeeded(vm), DispatcherPriority.Loaded);   // 載入表單時欄位一個一個設定，等整個表單設好再看有沒有錯誤
                };
                ExpandAdvancedWhenNeeded(vm);
            }
        };
    }

    private void ExpandAdvancedWhenNeeded(McpServersViewModel vm)
    {
        if (vm.HasEditor && vm.AdvancedHasError)
            AdvancedExpander.IsExpanded = true;
    }
}
