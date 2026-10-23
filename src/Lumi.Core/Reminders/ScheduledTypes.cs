namespace Lumi.Core.Reminders;

/// <summary>項目的種類。數值與舊版 schedules.json 相同（M13 匯入時不需要翻譯）。</summary>
public enum ScheduledKind
{
    /// <summary>提醒（指定時間響一次，或週期響）。</summary>
    Reminder = 0,

    /// <summary>倒數計時（開始時算出的時間響一次）。</summary>
    Timer = 1,

    /// <summary>排程（可週期）。</summary>
    Schedule = 2,
}

/// <summary>
/// 項目的狀態。數值與舊版相同。<see cref="Running"/> 是舊版 RunPrompt 用的，M10 不使用，保留數值以免之後的版本錯位。
/// </summary>
public enum ScheduledStatus
{
    Pending = 0,

    /// <summary>保留（M10 不使用）。</summary>
    Running = 1,

    Completed = 2,
    Cancelled = 3,
    Missed = 4,
}

/// <summary>週期種類。數值與舊版相同。</summary>
public enum RecurrenceKind
{
    /// <summary>每天。</summary>
    Daily = 0,

    /// <summary>週一至週五。</summary>
    Weekdays = 1,

    /// <summary>每週特定的星期幾。</summary>
    Weekly = 2,

    /// <summary>每月固定的日期（當月沒有那一天時取當月最後一天）。</summary>
    Monthly = 3,
}

/// <summary>建立者。儲存時用名稱（"Agent"／"User"），與舊版 CreatedBy 字串相同。</summary>
public enum ScheduledCreator
{
    Agent,
    User,
}
