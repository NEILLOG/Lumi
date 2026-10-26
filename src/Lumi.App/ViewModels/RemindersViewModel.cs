using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Reminders;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定頁「提醒排程」：列出所有提醒、可刪除、可清除已結束。不提供新增，也不切換啟停（只顯示「已暫停」）。
/// 內容經由 <see cref="ReminderService"/>；它的變更訊號（<see cref="ReminderService.ChangeToken"/>）換新時重新載入，
/// 所以工具新增或取消的項目會自動出現。建構子簽章固定，DI 與 SettingsViewModel 依賴它。
/// </summary>
public sealed partial class RemindersViewModel : ObservableObject, IDisposable
{
    private const int ListLimit = 500;

    private readonly ReminderService _reminders;
    private readonly IConfirmService _confirm;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenRegistration _registration;
    private int _generation;
    private bool _disposed;

    public RemindersViewModel(ReminderService reminders, IConfirmService confirm, IUiDispatcher dispatcher, TimeProvider time)
    {
        _reminders = reminders;
        _confirm = confirm;
        _dispatcher = dispatcher;
        Subscribe();
        _ = ReloadAsync();
    }

    /// <summary>待觸發的項目（維持 store 的順序：最早的在最上面）。</summary>
    public ObservableCollection<ReminderRowViewModel> Pending { get; } = [];

    /// <summary>已結束的項目（最近的在最上面）。</summary>
    public ObservableCollection<ReminderRowViewModel> Finished { get; } = [];

    public bool HasPending => Pending.Count > 0;

    public bool HasFinished => Finished.Count > 0;

    public bool ShowPendingEmpty => !HasPending;

    public bool ShowFinishedEmpty => !HasFinished;

    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public bool HasMessage => Message is not null;

    /// <summary>重新載入全部項目並重建兩個清單。較晚開始的載入會讓較早的結果作廢。</summary>
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        try
        {
            var items = await _reminders.ListAsync(new ReminderQuery(null, null, ListLimit), CancellationToken.None);
            if (_disposed || generation != _generation)
                return;
            Rebuild(items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_disposed)
                SetMessage(Strings.Reminders_LoadFailed, isError: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _registration.Dispose();
    }

    /// <summary>清除所有已結束的項目（先確認）。沒有已結束的項目時停用。</summary>
    [RelayCommand(CanExecute = nameof(HasFinished))]
    private async Task ClearFinishedAsync()
    {
        var confirmed = await _confirm.ConfirmDestructiveAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Reminders_ClearFinishedQuestion, Finished.Count),
            Strings.Reminders_ClearFinished);
        if (!confirmed)
            return;

        try
        {
            var removed = await _reminders.DeleteFinishedAsync(CancellationToken.None);
            SetMessage(string.Format(CultureInfo.CurrentCulture, Strings.Reminders_Cleared, removed), isError: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetMessage(Strings.Reminders_DeleteFailed, isError: true);
        }
        await ReloadAsync();
    }

    /// <summary>刪除一筆（任何狀態）。先確認；項目已經不存在也當成成功，重新載入即可。</summary>
    private async Task DeleteAsync(ReminderRowViewModel row)
    {
        var confirmed = await _confirm.ConfirmDestructiveAsync($"{Strings.Reminders_DeleteQuestion}\n{row.Title}", Strings.Reminders_Delete);
        if (!confirmed)
            return;

        try
        {
            await _reminders.DeleteAsync(row.Id, CancellationToken.None);
            SetMessage(Strings.Reminders_Deleted, isError: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetMessage(Strings.Reminders_DeleteFailed, isError: true);
            return;
        }
        await ReloadAsync();
    }

    /// <summary>
    /// 先取得權杖、之後才讀資料（<see cref="ReloadAsync"/>），變動就不會漏掉。權杖被取消時回到 UI 執行緒重新載入，並換成新的權杖。
    /// </summary>
    private void Subscribe()
    {
        var registration = _reminders.ChangeToken.Register(() => _dispatcher.Post(OnChanged));
        _registration.Dispose();
        _registration = registration;
    }

    private void OnChanged()
    {
        if (_disposed)
            return;
        Subscribe();
        _ = ReloadAsync();
    }

    private void Rebuild(IReadOnlyList<ScheduledItem> items)
    {
        Pending.Clear();
        Finished.Clear();
        foreach (var item in items)
        {
            var row = new ReminderRowViewModel(item, DeleteAsync);
            if (item.Status == ScheduledStatus.Pending)
                Pending.Add(row);
            else
                Finished.Insert(0, row);
        }

        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(ShowPendingEmpty));
        OnPropertyChanged(nameof(ShowFinishedEmpty));
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }

    private void SetMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}

/// <summary>清單的一列：提醒的唯讀快照（時間文字已依項目時區換算好），刪除透過 <see cref="DeleteCommand"/>。</summary>
public sealed class ReminderRowViewModel
{
    private const string TimeFormat = "yyyy/MM/dd HH:mm";

    public ReminderRowViewModel(ScheduledItem item, Func<ReminderRowViewModel, Task> delete)
    {
        Item = item;
        Id = item.Id;
        Title = item.Title;
        Message = item.Message;
        KindText = item.Kind switch
        {
            ScheduledKind.Timer => Strings.Reminders_KindTimer,
            ScheduledKind.Schedule => Strings.Reminders_KindSchedule,
            _ => Strings.Reminders_KindReminder,
        };
        StatusText = item.Status switch
        {
            ScheduledStatus.Pending when !item.Enabled => Strings.Reminders_StatusPaused,
            ScheduledStatus.Completed => Strings.Reminders_StatusCompleted,
            ScheduledStatus.Missed => Strings.Reminders_StatusMissed,
            ScheduledStatus.Cancelled => Strings.Reminders_StatusCancelled,
            _ => null,
        };
        ScheduleText = BuildScheduleText(item);
        CreatedByText = item.CreatedBy == ScheduledCreator.Agent ? Strings.Reminders_CreatedByAgent : Strings.Reminders_CreatedByUser;
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public ScheduledItem Item { get; }

    public string Id { get; }

    public string Title { get; }

    /// <summary>內容（可空白）。</summary>
    public string Message { get; }

    public string KindText { get; }

    /// <summary>待觸發且停用時是「已暫停」；已結束時是對應的結束狀態；其餘為 null（不顯示）。</summary>
    public string? StatusText { get; }

    public string? ScheduleText { get; }

    public string CreatedByText { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    private static string? BuildScheduleText(ScheduledItem item)
    {
        var zone = ScheduleCalculator.ResolveTimeZone(item.TimeZoneId);

        if (item.Status != ScheduledStatus.Pending)
            return item.LastFiredAt is { } fired
                ? string.Format(CultureInfo.CurrentCulture, Strings.Reminders_LastFiredFormat, FormatLocal(fired, zone))
                : null;

        var next = item.NextFireAt is { } fireAt
            ? string.Format(CultureInfo.CurrentCulture, Strings.Reminders_NextFireFormat, FormatLocal(fireAt, zone))
            : null;

        if (item.Trigger is not RecurrenceTrigger)
            return next;

        var describe = ScheduleCalculator.Describe(item.Trigger, item.TimeZoneId);
        return next is null ? describe : $"{describe} · {next}";
    }

    private static string FormatLocal(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString(TimeFormat, CultureInfo.InvariantCulture);
}
