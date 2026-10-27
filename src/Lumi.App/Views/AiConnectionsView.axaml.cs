using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 「AI 連線」頁的內容：預設模型、依連線分組的清單、詳細區（連線表單或模型表單）。DataContext 是 <see cref="AiSettingsViewModel"/>。
/// code-behind 只放視覺行為：清單的選取與 ViewModel 同步（點一列要先經過「放棄變更」的確認，使用者取消就把清單選回原本那一列），
/// 以及把焦點交給連線表單的金鑰輸入框。
/// </summary>
public partial class AiConnectionsView : UserControl
{
    private bool _syncing;

    public AiConnectionsView()
    {
        InitializeComponent();
        AiList.SelectionChanged += OnSelectionChanged;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AiSettingsViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AiSettingsViewModel.SelectedItem))
                        SyncSelection(vm);
                };
                SyncSelection(vm);
            }
        };
    }

    /// <summary>金鑰輸入框（缺金鑰流程要把焦點放進去）。</summary>
    public void FocusKeyBox() => ConnectionsPanel.FocusKeyBox();

    /// <summary>把清單的選取對齊 ViewModel 實際的選取。</summary>
    private void SyncSelection(AiSettingsViewModel vm)
    {
        if (vm.IsRebuilding || Equals(AiList.SelectedItem, vm.SelectedItem))
            return;
        _syncing = true;
        try
        {
            AiList.SelectedItem = vm.SelectedItem;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || DataContext is not AiSettingsViewModel vm || vm.IsRebuilding)
            return;
        var picked = AiList.SelectedItem as AiListItem;
        if (picked is null || Equals(picked, vm.SelectedItem))
        {
            if (picked is null)
                Dispatcher.UIThread.Post(() => SyncSelection(vm));   // 清單不能取消選取（Ctrl＋點擊）：選回去
            return;
        }
        _ = SelectAsync(vm, picked);
    }

    private async Task SelectAsync(AiSettingsViewModel vm, AiListItem picked)
    {
        await vm.SelectAsync(picked);
        // 使用者放棄切換時，ViewModel 的選取沒變：把清單選回原本那一列（在目前的選取事件處理完之後）
        Dispatcher.UIThread.Post(() => SyncSelection(vm));
    }
}
