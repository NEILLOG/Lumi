using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.ViewModels;

/// <summary>
/// 歷史抽屜「排程」分頁（M24 包 24E）：每個排程一組（組標題是名稱、副標是規則與下次時間），組內是執行紀錄（新到舊）。
/// 排程來自 <see cref="ReminderService"/>（只取跑 prompt 的項目），紀錄來自 <see cref="IScheduledRunStore"/>。
/// 執行紀錄變動（<see cref="IScheduledRunService.RunUpdated"/>）與提醒的變更訊號都會重新載入。
/// 沒有接任何服務時（<see cref="CreateInert"/>）清單永遠是空的，讓歷史抽屜在沒有排程執行器時也能組裝。
/// </summary>
public sealed partial class ScheduledRunsViewModel : ObservableObject, IDisposable
{
    private const int ListLimit = 500;

    private readonly IScheduledRunStore? _store;
    private readonly ReminderService? _reminders;
    private readonly IScheduledRunService? _service;
    private readonly Func<IScheduledRunViewer>? _viewer;
    private readonly ISettingsWindowService? _settings;
    private readonly IConfirmService? _confirm;
    private readonly IUiDispatcher? _dispatcher;
    private readonly TimeProvider _time;
    private readonly ScheduledRunOptions _options;
    private readonly ILogger<ScheduledRunsViewModel> _logger;
    private CancellationTokenRegistration _changeRegistration;
    private int _generation;
    private bool _disposed;

    /// <summary>
    /// 完整版本。<paramref name="viewer"/> 用委派取得：工作台本身會建立這個 ViewModel 的歷史抽屜，直接注入會循環相依。
    /// </summary>
    public ScheduledRunsViewModel(IScheduledRunStore store, ReminderService reminders, IScheduledRunService service,
        Func<IScheduledRunViewer> viewer, ISettingsWindowService settings, IConfirmService confirm, IUiDispatcher dispatcher,
        TimeProvider time, ScheduledRunOptions options, ILogger<ScheduledRunsViewModel>? logger = null)
    {
        _store = store;
        _reminders = reminders;
        _service = service;
        _viewer = viewer;
        _settings = settings;
        _confirm = confirm;
        _dispatcher = dispatcher;
        _time = time;
        _options = options;
        _logger = logger ?? NullLogger<ScheduledRunsViewModel>.Instance;

        service.RunUpdated += OnRunUpdated;
        SubscribeReminders();
        _ = ReloadAsync();
    }

    private ScheduledRunsViewModel()
    {
        _time = TimeProvider.System;
        _options = new ScheduledRunOptions();
        _logger = NullLogger<ScheduledRunsViewModel>.Instance;
    }

    /// <summary>沒有排程服務的空白版本：清單是空的，動作都不做事。</summary>
    public static ScheduledRunsViewModel CreateInert() => new();

    /// <summary>每個排程一組（有紀錄的在前，依最新一筆由新到舊；還沒有紀錄的排程在後）。</summary>
    public ObservableCollection<ScheduledGroupViewModel> Groups { get; } = [];

    /// <summary>清單是空的（沒有任何排程，也沒有任何紀錄）：顯示空狀態。</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>有未讀的紀錄：「排程」標籤帶圓點。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabName))]
    private bool _hasUnread;

    /// <summary>「排程」標籤的可存取名稱（有未讀時標出來）。</summary>
    public string TabName => HasUnread ? Strings.Schedule_TabRunsUnread : Strings.Schedule_TabRuns;

    /// <summary>清單上方的一行提示（立即執行、停用、啟用的結果）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => Notice is not null;

    /// <summary>底部說明：每個排程保留最近幾次紀錄。</summary>
    public string KeepNote => string.Format(CultureInfo.CurrentCulture, Strings.Schedule_RunsKeepNoteFormat, _options.KeepRunsPerSchedule);

    /// <summary>從設定頁管理排程（開在提醒排程分頁）。</summary>
    [RelayCommand]
    private async Task ManageInSettingsAsync()
    {
        if (_settings is null)
            return;
        await _settings.ShowAsync(new SettingsNavigation(SettingsSection.Reminders));
    }

    /// <summary>重新載入排程與紀錄。較晚開始的載入會讓較早的結果作廢。</summary>
    public async Task ReloadAsync()
    {
        if (_store is null || _reminders is null)
            return;
        var generation = ++_generation;
        try
        {
            var groups = await _store.ListGroupedAsync(_options.KeepRunsPerSchedule, CancellationToken.None);
            var items = (await _reminders.ListAsync(new ReminderQuery(null, null, ListLimit), CancellationToken.None))
                .Where(item => item.Action is RunPromptAction)
                .ToList();
            var unread = await _store.CountUnreadAsync(CancellationToken.None);
            if (_disposed || generation != _generation)
                return;
            Rebuild(groups, items, unread > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "載入排程紀錄失敗");
        }
    }

    /// <summary>點紀錄列：打開紀錄檢視（錯過的紀錄沒有對話，也能開）。</summary>
    public async Task OpenRunAsync(ScheduledRunRowViewModel row)
    {
        if (_viewer is null)
            return;
        try
        {
            await _viewer().OpenScheduledRunAsync(row.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "打開排程紀錄失敗");
        }
    }

    /// <summary>組的「立即執行」：不改變下次排定時間；排程不存在時不顯示提示。</summary>
    public async Task RunNowAsync(ScheduledGroupViewModel group)
    {
        if (_service is null || group.IsDeleted)
            return;
        var run = await _service.RunNowAsync(group.ScheduleId, CancellationToken.None);
        if (run is not null)
            Notice = string.Format(CultureInfo.CurrentCulture, Strings.Schedule_RunNowStartedFormat, group.Title);
        await ReloadAsync();
    }

    /// <summary>組的「停用／重新啟用」。只有待觸發的排程能啟停；結果用一行提示說明。</summary>
    public async Task ToggleEnabledAsync(ScheduledGroupViewModel group)
    {
        if (_reminders is null || group.IsDeleted)
            return;
        var enable = !group.Enabled;
        var item = await _reminders.SetEnabledAsync(group.ScheduleId, enable, CancellationToken.None);
        if (item is not null)
        {
            Notice = string.Format(CultureInfo.CurrentCulture,
                enable ? Strings.Schedule_EnabledFormat : Strings.Schedule_DisabledFormat, group.Title);
        }
        await ReloadAsync();
    }

    /// <summary>組的「在設定中編輯」：打開設定並選取這個排程。</summary>
    public async Task EditInSettingsAsync(ScheduledGroupViewModel group)
    {
        if (_settings is null || group.IsDeleted)
            return;
        await _settings.ShowAsync(new SettingsNavigation(SettingsSection.Reminders, ScheduleId: group.ScheduleId));
    }

    /// <summary>已刪除排程的組：刪除這個排程的全部紀錄（先確認；進行中的由服務略過）。</summary>
    public async Task DeleteRunsAsync(ScheduledGroupViewModel group)
    {
        if (_confirm is null || _service is null)
            return;
        var message = string.Format(CultureInfo.CurrentCulture, Strings.Schedule_DeleteRunsQuestionFormat, group.Title);
        if (!await _confirm.ConfirmDestructiveAsync(message, Strings.Schedule_DeleteRuns))
            return;
        await _service.DeleteRunsForScheduleAsync(group.ScheduleId, CancellationToken.None);
        Notice = null;
        await ReloadAsync();
    }

    /// <summary>刪除一筆紀錄（先確認）。進行中（執行中、待確認）的紀錄不能刪，直接不做事。</summary>
    public async Task DeleteRunAsync(ScheduledRunRowViewModel row)
    {
        if (_confirm is null || _service is null || row.IsActive)
            return;
        if (!await _confirm.ConfirmDestructiveAsync(Strings.Schedule_DeleteRunQuestion, Strings.Confirm_Delete))
            return;
        await _service.DeleteRunAsync(row.Id, CancellationToken.None);
        await ReloadAsync();
    }

    /// <summary>今天顯示 <c>HH:mm</c>，其他顯示 <c>M/d</c>（同歷史抽屜的對話列）。</summary>
    public string FormatTime(DateTimeOffset time)
    {
        var now = _time.GetLocalNow();
        var local = TimeZoneInfo.ConvertTime(time, _time.LocalTimeZone);
        return local.Date == now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("M/d", CultureInfo.InvariantCulture);
    }

    /// <summary>執行紀錄有變動（背景執行緒發出）：切回 UI 執行緒重新載入。</summary>
    private void OnRunUpdated(object? sender, ScheduledRun run)
    {
        if (_dispatcher is null)
            return;
        _dispatcher.Post(() =>
        {
            if (!_disposed)
                _ = ReloadAsync();
        });
    }

    /// <summary>
    /// 先取得權杖、之後才讀資料（<see cref="ReloadAsync"/>），變動就不會漏掉。權杖被取消時回到 UI 執行緒重新載入，並換成新的權杖。
    /// </summary>
    private void SubscribeReminders()
    {
        if (_reminders is null || _dispatcher is null)
            return;
        var registration = _reminders.ChangeToken.Register(() => _dispatcher.Post(OnRemindersChanged));
        _changeRegistration.Dispose();
        _changeRegistration = registration;
    }

    private void OnRemindersChanged()
    {
        if (_disposed)
            return;
        SubscribeReminders();
        _ = ReloadAsync();
    }

    private void Rebuild(IReadOnlyList<ScheduledRunGroup> groups, IReadOnlyList<ScheduledItem> items, bool hasUnread)
    {
        var byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        Groups.Clear();

        foreach (var group in groups)
        {
            byId.TryGetValue(group.ScheduleId, out var item);
            placed.Add(group.ScheduleId);
            var rows = group.Runs.Select(run => new ScheduledRunRowViewModel(this, run, FormatTime(run.PlannedAt))).ToList();
            Groups.Add(BuildGroup(group.ScheduleId, item, group.ScheduleTitle, rows));
        }

        // 有排程但還沒有任何紀錄：也要有一組（立即執行、停用、編輯都在這一組的「⋯」）
        foreach (var item in items.Where(item => !placed.Contains(item.Id)))
            Groups.Add(BuildGroup(item.Id, item, item.Title, []));

        HasUnread = hasUnread;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private ScheduledGroupViewModel BuildGroup(string scheduleId, ScheduledItem? item, string fallbackTitle,
        IReadOnlyList<ScheduledRunRowViewModel> rows)
    {
        var title = item?.Title ?? fallbackTitle;
        var subtitle = Subtitle(item);
        var enabled = item?.Enabled ?? false;
        return new ScheduledGroupViewModel(this, scheduleId, title, subtitle, isDeleted: item is null, enabled, rows);
    }

    /// <summary>副標：「規則 · 下次 …」；停用時「規則 · 已停用」；排程已被刪除（只剩紀錄）時「已刪除」。</summary>
    private string Subtitle(ScheduledItem? item)
    {
        if (item is null)
            return Strings.Schedule_GroupDeleted;

        var rule = ScheduleCalculator.Describe(item.Trigger, item.TimeZoneId);
        if (!item.Enabled)
            return string.Format(CultureInfo.CurrentCulture, Strings.Schedule_GroupSubFormat, rule, Strings.Schedule_GroupDisabled);

        // 單次的時間就是規則本身，不再重複列下次時間
        if (item.Trigger is OneTimeTrigger || item.NextFireAt is not { } next || item.Status != ScheduledStatus.Pending)
            return rule;

        var zone = ScheduleCalculator.ResolveTimeZone(item.TimeZoneId);
        var nextText = TimeZoneInfo.ConvertTime(next, zone).ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.CurrentCulture, Strings.Schedule_GroupSubFormat, rule,
            string.Format(CultureInfo.CurrentCulture, Strings.Schedule_NextFormat, nextText));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _changeRegistration.Dispose();
        if (_service is not null)
            _service.RunUpdated -= OnRunUpdated;
    }
}
