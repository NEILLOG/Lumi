using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 歷史抽屜的 code-behind 只放鍵盤與焦點的視覺行為：Esc 清除搜尋、列上的 F2／Delete／Shift+F10、改名輸入框的 Enter／Esc、
/// 改名時把焦點移到輸入框、群組標題列不能選取。動作本身都在 <see cref="HistoryViewModel"/> 的命令裡。
/// </summary>
public partial class HistoryDrawer : UserControl
{
    private HistoryViewModel? _subscribed;

    /// <summary>抽屜裡的清單（只有一個 ListBox）。</summary>
    private ListBox HistoryList => this.GetVisualDescendants().OfType<ListBox>().First();

    public HistoryDrawer()
    {
        InitializeComponent();
        ClearSearchButton.Click += (_, _) =>
        {
            if (ViewModel is { } vm)
                vm.SearchText = "";
            SearchBox.Focus();
        };
        DataContextChanged += (_, _) => Subscribe(DataContext as HistoryViewModel);
    }

    private HistoryViewModel? ViewModel => DataContext as HistoryViewModel;

    private void Subscribe(HistoryViewModel? vm)
    {
        if (ReferenceEquals(vm, _subscribed))
            return;
        if (_subscribed is not null)
            _subscribed.EditStarted -= OnEditStarted;
        _subscribed = vm;
        if (vm is not null)
            vm.EditStarted += OnEditStarted;
    }

    /// <summary>Esc 清除搜尋（有輸入時才處理，沒有輸入就讓事件往上走）。</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel is not { } vm || vm.SearchText.Length == 0)
            return;
        vm.SearchText = "";
        e.Handled = true;
    }

    /// <summary>列上的鍵盤：F2 改名、Delete 刪除、Shift+F10（或選單鍵）開選單；改名輸入框裡 Enter 確認、Esc 取消。</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Control source)
            return;
        var item = source.DataContext as ConversationListItem;

        if (source is TextBox && item is { IsEditing: true })
        {
            if (e.Key == Key.Enter)
            {
                vm.CommitRenameCommand.Execute(item);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelRenameCommand.Execute(item);
                e.Handled = true;
            }
            return;
        }

        if (item is null)
            return;   // 群組標題列
        switch (e.Key)
        {
            case Key.F2:
                vm.RenameCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.Delete:
                if (vm.DeleteCommand.CanExecute(item))
                    vm.DeleteCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.F10 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Apps:
                ShowRowMenu(source);
                e.Handled = true;
                break;
        }
    }

    /// <summary>右鍵：和「…」開同一個選單。</summary>
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source || source.DataContext is not ConversationListItem)
            return;
        ShowRowMenu(source);
        e.Handled = true;
    }

    /// <summary>改名輸入框失去焦點就放棄改名（不自動儲存）。</summary>
    private void OnListLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && e.Source is TextBox && ((Control)e.Source).DataContext is ConversationListItem { IsEditing: true } item)
            vm.CancelRenameCommand.Execute(item);
    }

    /// <summary>群組標題列不能用鍵盤或滑鼠選取（ListBox 的列是重用的，每次準備列都要重新判斷）。</summary>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem container)
            container.IsEnabled = container.DataContext is not HistoryGroupHeader;
    }

    /// <summary>改名開始：等輸入框顯示出來後把焦點移過去並全選文字（字數上限由列的 <see cref="ConversationListItem.TitleMaxLength"/> 綁定）。</summary>
    private void OnEditStarted(object? sender, ConversationListItem item) => Dispatcher.UIThread.Post(() =>
    {
        var editor = HistoryList.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => ReferenceEquals(box.DataContext, item) && box.IsEffectivelyVisible);
        if (editor is null)
            return;
        editor.Focus();
        editor.SelectAll();
    }, DispatcherPriority.Loaded);

    /// <summary>
    /// 列的選單開啟時：把命令與參數接到這一列的對話上（<see cref="FlyoutBase.Target"/> 是列上的「…」按鈕，它的 DataContext 是這一列）。
    /// 刪除的 Tooltip 寫出不能刪除的原因（回應中）。
    /// </summary>
    private void OnRowMenuOpening(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm || sender is not MenuFlyout menu || menu.Target?.DataContext is not ConversationListItem item)
            return;
        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            entry.CommandParameter = item;
            if (entry.Tag as string == "rename")
            {
                entry.Command = vm.RenameCommand;
            }
            else
            {
                entry.Command = vm.DeleteCommand;
                ToolTip.SetTip(entry, item.DeleteTip);
                ToolTip.SetShowOnDisabled(entry, true);
            }
        }
    }

    /// <summary>開啟列的「…」選單（Shift+F10、選單鍵、右鍵共用）。</summary>
    private static void ShowRowMenu(Control source)
    {
        var row = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        var more = row?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("row-more"));
        if (more is not null)
            FlyoutBase.ShowAttachedFlyout(more);
    }
}
