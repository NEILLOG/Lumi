namespace Lumi.Core.Reminders;

/// <summary>
/// 篩選條件。<c>null</c> 表示不限；集合是空的表示什麼都不符合（回空清單）。
/// </summary>
/// <param name="Limit">最多幾筆；null 表示不限，設了必須 ≥ 1。</param>
public sealed record ScheduledItemFilter(
    IReadOnlyCollection<ScheduledStatus>? Statuses = null,
    IReadOnlyCollection<ScheduledKind>? Kinds = null,
    int? Limit = null);

/// <summary>
/// 提醒與排程項目的存取。列表一律依 <c>NextFireAt ?? CreatedAt</c> 由早到晚排，同時間的依 Id 排。
/// </summary>
public interface IScheduledItemStore
{
    /// <summary>新增一筆。Id 重複時丟例外（上限由呼叫端 <see cref="ReminderOptions"/> 檢查，這裡不管）。</summary>
    Task AddAsync(ScheduledItem item, CancellationToken ct = default);

    /// <summary>以 Id 找到的那一筆整筆換掉；找不到回 false。</summary>
    Task<bool> UpdateAsync(ScheduledItem item, CancellationToken ct = default);

    /// <summary>找不到回 null。</summary>
    Task<ScheduledItem?> GetAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledItem>> ListAsync(ScheduledItemFilter filter, CancellationToken ct = default);

    /// <summary>狀態為 Pending 的筆數，含停用的（上限是算這個數字）。</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>已啟用、Pending、且 <c>NextFireAt</c> 不晚於 <paramref name="now"/> 的項目，依觸發時間由早到晚。</summary>
    Task<IReadOnlyList<ScheduledItem>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>已啟用且 Pending 的項目中，最早的 <c>NextFireAt</c>；沒有時回 null。</summary>
    Task<DateTimeOffset?> GetNextFireAtAsync(CancellationToken ct = default);

    /// <summary>
    /// 刪除已結束（Completed、Cancelled、Missed）且 <c>LastFiredAt ?? NextFireAt ?? CreatedAt</c> 早於
    /// <paramref name="olderThan"/> 的項目，回傳刪除筆數。
    /// </summary>
    Task<int> PurgeFinishedAsync(DateTimeOffset olderThan, CancellationToken ct = default);

    /// <summary>以 Id 刪除一筆（任何狀態）；找不到回 false。</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>刪除所有已結束（Completed、Cancelled、Missed）的項目，不看時間；回傳刪除筆數。</summary>
    Task<int> DeleteFinishedAsync(CancellationToken ct = default);
}
