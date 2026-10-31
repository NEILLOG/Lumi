using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Reminders;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定頁「提醒排程」：列出所有提醒與排程、可啟停待觸發的項目、可編輯「跑 prompt」的排程、可刪除、可清除已結束。不提供新增。
/// 內容經由 <see cref="ReminderService"/>；它的變更訊號（<see cref="ReminderService.ChangeToken"/>）換新時重新載入，
/// 所以工具新增或取消的項目會自動出現。「跑 prompt」的紀錄（<see cref="IScheduledRunStore"/>、<see cref="IScheduledRunService"/>）只用在刪除時，
/// 沒有提供時刪除就只刪排程本身。建構子的前四個參數是原本的簽章，DI 與 SettingsHost 依賴它。
/// </summary>
public sealed partial class RemindersViewModel : ObservableObject, IDisposable
{
    private const int ListLimit = 500;

    private readonly ReminderService _reminders;
    private readonly IConfirmService _confirm;
    private readonly IUiDispatcher _dispatcher;
    private readonly IScheduledRunService? _scheduledRuns;
    private readonly IScheduledRunStore? _runStore;
    private readonly HashSet<string> _expandedIds = [];
    private CancellationTokenRegistration _registration;
    private int _generation;
    private bool _disposed;
    private string? _selectedId;
    private bool _revealPending;

    public RemindersViewModel(
        ReminderService reminders, IConfirmService confirm, IUiDispatcher dispatcher, TimeProvider time,
        IScheduledRunService? scheduledRuns = null, IScheduledRunStore? runStore = null, ReminderOptions? options = null)
    {
        _reminders = reminders;
        _confirm = confirm;
        _dispatcher = dispatcher;
        _scheduledRuns = scheduledRuns;
        _runStore = runStore;
        MaxPromptChars = (options ?? new ReminderOptions()).MaxPromptChars;
        Subscribe();
        _ = ReloadAsync();
    }

    /// <summary>待觸發的項目（維持 store 的順序：最早的在最上面）。</summary>
    public ObservableCollection<ReminderRowViewModel> Pending { get; } = [];

    /// <summary>已結束的項目（最近的在最上面）。</summary>
    public ObservableCollection<ReminderRowViewModel> Finished { get; } = [];

    /// <summary>「跑 prompt」的 prompt 字數上限（編輯用）。</summary>
    public int MaxPromptChars { get; }

    /// <summary>要求畫面捲到某一列（設定視窗帶著 ScheduleId 打開時）；參數是排程 Id。</summary>
    public event Action<string>? RevealRequested;

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

    /// <summary>
    /// 選取並展開某一筆（設定視窗的 <see cref="SettingsNavigation.ScheduleId"/>）。項目還沒載入時，載入完成後再套用。
    /// 只有「跑 prompt」的項目會展開編輯區。
    /// </summary>
    public void Reveal(string scheduleId)
    {
        _selectedId = scheduleId;
        _expandedIds.Add(scheduleId);
        _revealPending = true;
        ApplySelection();
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

    // ---- 列的操作（由 ReminderRowViewModel 呼叫） ----

    /// <summary>
    /// 刪除一筆（任何狀態）。先確認；「跑 prompt」的排程若有執行紀錄，再問一次要不要一併刪除（兩段式：沒有勾選框，見報告）。
    /// 項目已經不存在也當成成功，重新載入即可。
    /// </summary>
    internal async Task DeleteAsync(ReminderRowViewModel row)
    {
        var question = row.IsRunPrompt
            ? Strings.Reminders_DeleteRunPromptQuestion
            : $"{Strings.Reminders_DeleteQuestion}\n{row.Title}";
        var confirmed = await _confirm.ConfirmDestructiveAsync(question, Strings.Reminders_Delete);
        if (!confirmed)
            return;

        var runCount = 0;
        if (row.IsRunPrompt && _runStore is not null && _scheduledRuns is not null)
        {
            try
            {
                runCount = (await _runStore.ListForScheduleAsync(row.Id, CancellationToken.None)).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetMessage(Strings.Reminders_DeleteFailed, isError: true);
                return;
            }
        }
        var deleteRuns = runCount > 0 && await _confirm.ConfirmDestructiveAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Reminders_DeleteWithRunsFormat, runCount), Strings.Reminders_Delete);

        try
        {
            await _reminders.DeleteAsync(row.Id, CancellationToken.None);
            if (deleteRuns)
                await _scheduledRuns!.DeleteRunsForScheduleAsync(row.Id, CancellationToken.None);
            SetMessage(Strings.Reminders_Deleted, isError: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetMessage(Strings.Reminders_DeleteFailed, isError: true);
            return;
        }
        await ReloadAsync();
    }

    /// <summary>啟用或停用一筆待觸發的項目。失敗（例如一次性提醒已過期）時錯誤顯示在該列，並重新載入讓開關回到實際狀態。</summary>
    internal async Task SetEnabledAsync(ReminderRowViewModel row, bool enabled)
    {
        var error = await TryAsync(() => _reminders.SetEnabledAsync(row.Id, enabled, CancellationToken.None));
        if (error is null)
        {
            await ReloadAsync();
            return;
        }

        await ReloadAsync();
        if (FindRow(row.Id) is { } fresh)
            fresh.ErrorText = error;
    }

    /// <summary>儲存「跑 prompt」的 prompt、授權與範圍。時間不能在這裡改。</summary>
    internal async Task SaveAsync(ReminderRowViewModel row)
    {
        if (row.PromptError is not null)
            return;

        var grant = new ScheduleGrant(row.EditWrite, row.EditNetwork, row.EditDestructive);
        var scope = row.ScopeChanged ? row.EditScope.Select(entry => entry.Item).ToList() : null;
        var update = new PromptScheduleUpdate(row.EditPrompt.Trim(), grant, scope);

        try
        {
            var updated = await _reminders.UpdatePromptScheduleAsync(row.Id, update, CancellationToken.None);
            if (updated is null)
            {
                row.SetEditMessage(Strings.Reminders_EditSaveFailed, isError: true);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.SetEditMessage(Strings.Reminders_EditSaveFailed, isError: true);
            return;
        }

        await ReloadAsync();
        FindRow(row.Id)?.SetEditMessage(Strings.Reminders_EditSaved, isError: false);
    }

    internal void SetExpanded(ReminderRowViewModel row, bool expanded)
    {
        row.IsExpanded = expanded;
        if (expanded)
            _expandedIds.Add(row.Id);
        else
            _expandedIds.Remove(row.Id);
    }

    // ---- 內部 ----

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
            var row = new ReminderRowViewModel(item, this);
            row.IsExpanded = row.CanEdit && _expandedIds.Contains(item.Id);
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
        ApplySelection();
    }

    /// <summary>把選取與展開套用到目前的列；要求捲動的對象出現時發出 <see cref="RevealRequested"/>。</summary>
    private void ApplySelection()
    {
        var rows = Pending.Concat(Finished).ToList();
        foreach (var row in rows)
        {
            row.IsSelected = row.Id == _selectedId;
            if (row.IsSelected && row.CanEdit)
                row.IsExpanded = true;
        }

        if (_revealPending && _selectedId is { } id && rows.Any(r => r.Id == id))
        {
            _revealPending = false;
            RevealRequested?.Invoke(id);
        }
    }

    private ReminderRowViewModel? FindRow(string id) => Pending.Concat(Finished).FirstOrDefault(r => r.Id == id);

    /// <summary>執行一個會改變項目的動作；回傳錯誤文字（null 表示成功）。</summary>
    private static async Task<string?> TryAsync(Func<Task<ScheduledItem?>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (ReminderException ex)
        {
            return ex.Message;   // 業務錯誤的訊息是寫給使用者看的繁體中文
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Strings.Workbench_UnexpectedError;
        }
    }

    private void SetMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}

/// <summary>清單的一列：提醒的快照（時間文字已依項目時區換算好），啟停、展開編輯、儲存與刪除都透過 <see cref="RemindersViewModel"/>。</summary>
public sealed partial class ReminderRowViewModel : ObservableObject
{
    private const string TimeFormat = "yyyy/MM/dd HH:mm";

    private readonly RemindersViewModel _host;
    private readonly int _maxPromptChars;
    private bool _scopeChanged;

    public ReminderRowViewModel(ScheduledItem item, RemindersViewModel host)
    {
        Item = item;
        _host = host;
        _maxPromptChars = host.MaxPromptChars;
        Id = item.Id;
        Title = item.Title;
        Message = item.Message;
        KindText = item.Kind switch
        {
            ScheduledKind.Timer => Strings.Reminders_KindTimer,
            ScheduledKind.Schedule => Strings.Reminders_KindSchedule,
            _ => Strings.Reminders_KindReminder,
        };
        IsRunPrompt = item.Action is RunPromptAction;
        ActionText = IsRunPrompt ? Strings.Reminders_KindRunPrompt : Strings.Reminders_KindNotify;
        // 待觸發的「已暫停」由開關表達，這裡只剩已結束的狀態文字
        StatusText = item.Status switch
        {
            ScheduledStatus.Completed => Strings.Reminders_StatusCompleted,
            ScheduledStatus.Missed => Strings.Reminders_StatusMissed,
            ScheduledStatus.Cancelled => Strings.Reminders_StatusCancelled,
            _ => null,
        };
        ScheduleText = BuildScheduleText(item);
        CreatedByText = item.CreatedBy == ScheduledCreator.Agent ? Strings.Reminders_CreatedByAgent : Strings.Reminders_CreatedByUser;
        IsPending = item.Status == ScheduledStatus.Pending;
        Enabled = item.Enabled;
        SwitchTip = string.Format(CultureInfo.CurrentCulture, Strings.Reminders_EnabledSwitchTipFormat, Title);

        DeleteCommand = new AsyncRelayCommand(() => host.DeleteAsync(this));
        ToggleEnabledCommand = new AsyncRelayCommand(() => host.SetEnabledAsync(this, !Enabled));
        ToggleEditCommand = new RelayCommand(() => host.SetExpanded(this, !IsExpanded));
        SaveCommand = new AsyncRelayCommand(() => host.SaveAsync(this), () => PromptError is null);

        if (item.Action is RunPromptAction action)
        {
            EditPrompt = action.Prompt;
            EditWrite = action.Grant.Write;
            EditNetwork = action.Grant.Network;
            EditDestructive = action.Grant.Destructive;
            foreach (var scope in action.Scope)
                EditScope.Add(new ReminderScopeEntry(scope, RemoveScope));
        }
        EditScope.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoScope));
    }

    public ScheduledItem Item { get; }

    public string Id { get; }

    public string Title { get; }

    /// <summary>內容（可空白）。</summary>
    public string Message { get; }

    /// <summary>種類徽章：提醒、計時器或排程。</summary>
    public string KindText { get; }

    /// <summary>動作徽章：通知或跑 prompt。</summary>
    public string ActionText { get; }

    /// <summary>「跑 prompt」的項目（可以編輯）。</summary>
    public bool IsRunPrompt { get; }

    public bool CanEdit => IsRunPrompt;

    /// <summary>待觸發的項目才有啟停開關。</summary>
    public bool IsPending { get; }

    /// <summary>目前啟用中（開關的狀態；待觸發的項目才有意義）。</summary>
    public bool Enabled { get; }

    public string SwitchTip { get; }

    /// <summary>已結束的項目的狀態文字；待觸發的為 null（不顯示）。</summary>
    public string? StatusText { get; }

    public string? ScheduleText { get; }

    public string CreatedByText { get; }

    /// <summary>啟停失敗等錯誤（顯示在該列）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorText))]
    private string? _errorText;

    public bool HasErrorText => ErrorText is not null;

    /// <summary>目前這列被選取（設定視窗帶 ScheduleId 打開時）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>編輯區是否展開（只有「跑 prompt」可以）。</summary>
    [ObservableProperty]
    private bool _isExpanded;

    // ---- 編輯區（跑 prompt） ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptError), nameof(HasPromptError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _editPrompt = "";

    [ObservableProperty]
    private bool _editWrite;

    [ObservableProperty]
    private bool _editNetwork;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestructiveNote))]
    private bool _editDestructive;

    /// <summary>可以存取的資料夾（只能移除，不能新增）。</summary>
    public ObservableCollection<ReminderScopeEntry> EditScope { get; } = [];

    public bool HasNoScope => EditScope.Count == 0;

    /// <summary>範圍被移除過（儲存時才會送出新的範圍）。</summary>
    public bool ScopeChanged => _scopeChanged;

    /// <summary>破壞性操作的說明：勾選後換成「不會再問」的說明。</summary>
    public string DestructiveNote => EditDestructive ? Strings.Schedule_Grant_DestructiveOnNote : Strings.Schedule_Grant_DestructiveNote;

    /// <summary>prompt 的驗證錯誤（空白或超過上限）；沒有錯誤時為 null。</summary>
    public string? PromptError
    {
        get
        {
            var trimmed = EditPrompt.Trim();
            if (trimmed.Length == 0)
                return Strings.Reminders_EditPromptEmpty;
            return trimmed.Length > _maxPromptChars
                ? string.Format(CultureInfo.CurrentCulture, Strings.Reminders_EditPromptTooLongFormat, _maxPromptChars)
                : null;
        }
    }

    public bool HasPromptError => PromptError is not null;

    /// <summary>編輯區的結果訊息（已儲存、儲存失敗）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditMessage))]
    private string? _editMessage;

    public bool HasEditMessage => EditMessage is not null;

    [ObservableProperty]
    private bool _editMessageIsError;

    public IRelayCommand ToggleEditCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand ToggleEnabledCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    internal void SetEditMessage(string text, bool isError)
    {
        EditMessageIsError = isError;
        EditMessage = text;
    }

    private void RemoveScope(ReminderScopeEntry entry)
    {
        EditScope.Remove(entry);
        _scopeChanged = true;
        OnPropertyChanged(nameof(ScopeChanged));
    }

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

/// <summary>編輯區的一個資料夾（可以移除）。</summary>
public sealed class ReminderScopeEntry
{
    public ReminderScopeEntry(ScheduleScopeItem item, Action<ReminderScopeEntry> remove)
    {
        Item = item;
        DisplayName = item.DisplayName;
        RemoveTip = string.Format(CultureInfo.CurrentCulture, Strings.Reminders_EditScopeRemoveTipFormat, DisplayName);
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public ScheduleScopeItem Item { get; }

    public string DisplayName { get; }

    /// <summary>移除鈕的 Tooltip 與可存取名稱。</summary>
    public string RemoveTip { get; }

    public IRelayCommand RemoveCommand { get; }
}
