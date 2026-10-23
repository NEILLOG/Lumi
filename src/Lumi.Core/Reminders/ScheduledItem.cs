using System.Security.Cryptography;

namespace Lumi.Core.Reminders;

/// <summary>
/// 一筆提醒、計時器或排程。不可變：要改狀態時建立新的 record，再用 <see cref="IScheduledItemStore.UpdateAsync"/> 寫回。
/// </summary>
/// <param name="NextFireAt">下一次觸發時間；已結束的項目保留最後的值（可能是 null）。</param>
/// <param name="LastFiredAt">最後一次觸發時間。</param>
public sealed record ScheduledItem(
    string Id,
    ScheduledKind Kind,
    string Title,
    string Message,
    Trigger Trigger,
    string TimeZoneId,
    ScheduledAction Action,
    ScheduledStatus Status,
    bool Enabled,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFiredAt,
    ScheduledCreator CreatedBy,
    DateTimeOffset CreatedAt)
{
    /// <summary>8 碼十六進位字串（與舊版相同的格式）。</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}
