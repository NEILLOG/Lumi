using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 排程分頁的 code-behind 只放鍵盤與選單的接線：組標題列的 Shift+F10／選單鍵、紀錄列的 Delete、Shift+F10／選單鍵與右鍵。
/// 選單開啟時把命令接到組或紀錄（同歷史抽屜：Flyout 的內容不繼承列的 DataContext）。動作本身都在 <see cref="ScheduledRunsViewModel"/>。
/// </summary>
public partial class ScheduledRunsView : UserControl
{
    public ScheduledRunsView()
    {
        InitializeComponent();
    }

    /// <summary>組標題列：Shift+F10 或選單鍵開組的「⋯」選單。</summary>
    private void OnGroupHeadKeyDown(object? sender, KeyEventArgs e)
    {
        var opensMenu = e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (!opensMenu)
            return;
        if (sender is Visual head && head.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("group-more")) is { } more)
        {
            FlyoutBase.ShowAttachedFlyout(more);
            e.Handled = true;
        }
    }

    /// <summary>紀錄列：Delete 刪除這一筆（進行中的不做事）。</summary>
    private void OnRunListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Control source)
            return;

        switch (e.Key)
        {
            case Key.Delete when source.DataContext is ScheduledRunRowViewModel row:
                if (row.DeleteCommand.CanExecute(null))
                    row.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F10 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Apps:
                ShowRowMenu(source);
                e.Handled = true;
                break;
        }
    }

    /// <summary>右鍵：和紀錄列的「⋯」開同一個選單。</summary>
    private void OnRunContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source || source.DataContext is not ScheduledRunRowViewModel)
            return;
        ShowRowMenu(source);
        e.Handled = true;
    }

    /// <summary>組的選單：依組的狀態決定顯示哪些項目，並接上命令。已刪除的排程只有「刪除這些紀錄」。</summary>
    private void OnGroupMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || menu.Target?.DataContext is not ScheduledGroupViewModel group)
            return;
        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            switch (entry.Tag as string)
            {
                case "run":
                    entry.IsVisible = !group.IsDeleted;
                    entry.Command = group.RunNowCommand;
                    break;
                case "toggle":
                    entry.IsVisible = !group.IsDeleted;
                    entry.Header = group.ToggleText;
                    entry.Command = group.ToggleEnabledCommand;
                    break;
                case "edit":
                    entry.IsVisible = !group.IsDeleted;
                    entry.Command = group.EditInSettingsCommand;
                    break;
                case "delete":
                    entry.IsVisible = group.IsDeleted;
                    entry.Command = group.DeleteRunsCommand;
                    break;
            }
        }
    }

    /// <summary>紀錄的選單：刪除這一筆。進行中的紀錄停用，Tooltip 寫出原因。</summary>
    private void OnRunMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || menu.Target?.DataContext is not ScheduledRunRowViewModel row)
            return;
        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            entry.Command = row.DeleteCommand;
            entry.IsEnabled = row.DeleteCommand.CanExecute(null);
            ToolTip.SetTip(entry, row.DeleteTip);
            ToolTip.SetShowOnDisabled(entry, true);
        }
    }

    /// <summary>開啟紀錄列的「⋯」選單（Shift+F10、選單鍵、右鍵共用）。</summary>
    private static void ShowRowMenu(Control source)
    {
        var row = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        var more = row?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("run-more"));
        if (more is not null)
            FlyoutBase.ShowAttachedFlyout(more);
    }
}
