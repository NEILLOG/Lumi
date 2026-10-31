using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Reminders;

namespace Lumi.App.ViewModels;

/// <summary>
/// 排程分頁的一組（一個排程）：組標題、副標、「⋯」選單的命令，以及組內的紀錄列。
/// 組標題列不能選取；點紀錄列（<see cref="SelectedRun"/>）才會打開紀錄。
/// </summary>
public sealed partial class ScheduledGroupViewModel : ObservableObject
{
    private readonly ScheduledRunsViewModel _owner;
    private ScheduledRunRowViewModel? _selected;

    public ScheduledGroupViewModel(ScheduledRunsViewModel owner, string scheduleId, string title, string subtitle, bool isDeleted, bool enabled,
        IReadOnlyList<ScheduledRunRowViewModel> runs)
    {
        _owner = owner;
        ScheduleId = scheduleId;
        Title = title;
        Subtitle = subtitle;
        IsDeleted = isDeleted;
        Enabled = enabled;
        Runs = runs;
        RunNowCommand = new AsyncRelayCommand(() => _owner.RunNowAsync(this), () => !IsDeleted);
        ToggleEnabledCommand = new AsyncRelayCommand(() => _owner.ToggleEnabledAsync(this), () => !IsDeleted);
        EditInSettingsCommand = new AsyncRelayCommand(() => _owner.EditInSettingsAsync(this), () => !IsDeleted);
        DeleteRunsCommand = new AsyncRelayCommand(() => _owner.DeleteRunsAsync(this));
    }

    public string ScheduleId { get; }

    public string Title { get; }

    /// <summary>「規則 · 下次 …」、「規則 · 已停用」或「已刪除」。</summary>
    public string Subtitle { get; }

    /// <summary>排程已被刪除，只剩紀錄：「⋯」只有「刪除這些紀錄」。</summary>
    public bool IsDeleted { get; }

    /// <summary>排程目前是否啟用（停用時選單顯示「重新啟用」）。</summary>
    public bool Enabled { get; }

    public string ToggleText => Enabled ? Strings.Schedule_Disable : Strings.Schedule_Enable;

    /// <summary>組內的紀錄，由新到舊。</summary>
    public IReadOnlyList<ScheduledRunRowViewModel> Runs { get; }

    public bool HasRuns => Runs.Count > 0;

    public IAsyncRelayCommand RunNowCommand { get; }

    public IAsyncRelayCommand ToggleEnabledCommand { get; }

    public IAsyncRelayCommand EditInSettingsCommand { get; }

    public IAsyncRelayCommand DeleteRunsCommand { get; }

    /// <summary>
    /// 清單的選取＝點了哪一筆紀錄，就打開它（同歷史抽屜的對話列）。選取的值保留到清單重建為止。
    /// </summary>
    public ScheduledRunRowViewModel? SelectedRun
    {
        get => _selected;
        set
        {
            if (value is null || ReferenceEquals(value, _selected))
                return;
            _selected = value;
            OnPropertyChanged();
            _ = _owner.OpenRunAsync(value);
        }
    }
}

/// <summary>
/// 組內的一筆執行紀錄：狀態徽章、觸發方式（補跑、立即執行）、時間與未讀點。進行中的紀錄不能刪除。
/// </summary>
public sealed partial class ScheduledRunRowViewModel : ObservableObject
{
    public ScheduledRunRowViewModel(ScheduledRunsViewModel owner, ScheduledRun run, string timeText)
    {
        Run = run;
        TimeText = timeText;
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteRunAsync(this), () => !IsActive);
    }

    public ScheduledRun Run { get; }

    public string Id => Run.Id;

    public string ScheduleId => Run.ScheduleId;

    /// <summary>排定時間（今天 HH:mm，其他 M/d）。</summary>
    public string TimeText { get; }

    public string StatusText => Run.Status switch
    {
        ScheduledRunStatus.Running => Strings.Schedule_Status_Running,
        ScheduledRunStatus.AwaitingApproval => Strings.Schedule_Status_AwaitingApproval,
        ScheduledRunStatus.Completed => Strings.Schedule_Status_Completed,
        ScheduledRunStatus.Failed => Strings.Schedule_Status_Failed,
        ScheduledRunStatus.Missed => Strings.Schedule_Status_Missed,
        ScheduledRunStatus.Interrupted => Strings.Schedule_Status_Interrupted,
        _ => Run.Status.ToString(),
    };

    /// <summary>補跑或立即執行才有的觸發方式徽章；照排程到點的是 null。</summary>
    public string? TriggerText => Run.Trigger switch
    {
        ScheduledRunTrigger.CatchUp => Strings.Schedule_Trigger_CatchUp,
        ScheduledRunTrigger.Manual => Strings.Schedule_Trigger_Manual,
        _ => null,
    };

    public bool HasTrigger => TriggerText is not null;

    public bool IsUnread => Run.IsUnread;

    /// <summary>未讀圓點的不透明度（沒有未讀時保留版面但看不到）。</summary>
    public double DotOpacity => IsUnread ? 1 : 0;

    /// <summary>執行中或待確認：不能刪除。</summary>
    public bool IsActive => Run.IsActive;

    /// <summary>刪除被擋住時的原因（Tooltip）；可以刪除時是空字串。</summary>
    public string DeleteTip => IsActive ? Strings.Schedule_DeleteRunBlocked : "";

    /// <summary>可存取名稱：狀態、觸發方式、時間，未讀時加上「未讀」。</summary>
    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { StatusText };
            if (TriggerText is { } trigger)
                parts.Add(trigger);
            parts.Add(TimeText);
            if (IsUnread)
                parts.Add(Strings.Schedule_Unread);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>「刪除」這一筆（選單與 Delete 鍵）。</summary>
    public IAsyncRelayCommand DeleteCommand { get; }
}
