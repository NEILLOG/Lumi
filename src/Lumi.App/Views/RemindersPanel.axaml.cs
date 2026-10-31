using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 設定頁「提醒排程」的內容（M20 20B）。行為全部在 <c>RemindersViewModel</c>；code-behind 只負責「要求捲到某一列」
/// （設定視窗帶著 ScheduleId 打開時，ViewModel 發出 <see cref="RemindersViewModel.RevealRequested"/>）。
/// </summary>
public partial class RemindersPanel : UserControl
{
    private RemindersViewModel? _subscribed;

    public RemindersPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe(DataContext as RemindersViewModel);
    }

    private void Subscribe(RemindersViewModel? vm)
    {
        if (ReferenceEquals(vm, _subscribed))
            return;
        if (_subscribed is not null)
            _subscribed.RevealRequested -= OnRevealRequested;
        _subscribed = vm;
        if (_subscribed is not null)
            _subscribed.RevealRequested += OnRevealRequested;
    }

    private void OnRevealRequested(string scheduleId) =>
        // 展開後版面才會更新，等排版完成再捲動
        Dispatcher.UIThread.Post(() => BringRowIntoView(scheduleId), DispatcherPriority.Loaded);

    private void BringRowIntoView(string scheduleId)
    {
        var row = this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("reminder-row") && b.DataContext is ReminderRowViewModel r && r.Id == scheduleId);
        row?.BringIntoView();
    }
}
