namespace Lumi.Core.Reminders;

/// <summary>建立提醒或排程的請求。<paramref name="TimeZoneId"/> 為 null 或空白時用本機時區。</summary>
public sealed record NewReminder(
    string Title,
    string Message,
    Trigger Trigger,
    string? TimeZoneId,
    ScheduledKind Kind,
    ScheduledCreator CreatedBy);

/// <summary>列出項目的條件；<c>null</c> 的集合表示不限。</summary>
public sealed record ReminderQuery(
    IReadOnlyCollection<ScheduledStatus>? Statuses,
    IReadOnlyCollection<ScheduledKind>? Kinds,
    int Limit);

/// <summary>
/// 取消的結果。<see cref="Item"/> 為 null 表示找不到；<see cref="Changed"/> 為 false 表示項目已經結束，沒有變更。
/// </summary>
public sealed record CancelResult(ScheduledItem? Item, bool Changed);

/// <summary>提醒、計時器與排程的入口（工具呼叫這裡）。實作只有 <see cref="ReminderService"/>。</summary>
public interface IReminderService
{
    /// <summary>建立提醒（一次性或週期）。驗證失敗丟 <see cref="ReminderException"/>，訊息可以直接給模型看。</summary>
    Task<ScheduledItem> CreateReminderAsync(NewReminder request, CancellationToken ct);

    /// <summary>開始計時：<paramref name="durationSeconds"/> 秒後響一次。<paramref name="label"/> 為 null 或空白時用「計時器」。</summary>
    Task<ScheduledItem> StartTimerAsync(string? label, int durationSeconds, CancellationToken ct);

    Task<IReadOnlyList<ScheduledItem>> ListAsync(ReminderQuery query, CancellationToken ct);

    Task<CancelResult> CancelAsync(string id, CancellationToken ct);

    /// <summary>只有待觸發（Pending）的項目可以啟停；找不到回 null。</summary>
    Task<ScheduledItem?> SetEnabledAsync(string id, bool enabled, CancellationToken ct);

    /// <summary>真正刪除一筆（任何狀態，待觸發的刪除後不會再響）；找不到回 false。設定頁用，不是工具。</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct);

    /// <summary>刪除所有已結束（完成、取消、錯過）的項目；回傳刪除筆數。設定頁用，不是工具。</summary>
    Task<int> DeleteFinishedAsync(CancellationToken ct);
}
