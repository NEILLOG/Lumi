using System.Globalization;

namespace Lumi.Core.Reminders;

/// <summary>
/// 觸發時間的計算（純函式，不讀時鐘，時間一律由呼叫端傳入）。
/// 週期以項目時區的當地時間計算；結果是絕對時間（<see cref="DateTimeOffset"/>）。
/// 夏令時間：當地時間不存在（春季前進的空窗）→ 順延到空窗結束的那一刻；重複（秋季後退）→ 取較早的那次。
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>向後搜尋的上限（天）。超過仍找不到就回 null。</summary>
    public const int SearchLimitDays = 730;

    // 任何時區的 UTC 偏移都在 ±14 小時內，所以夏令時間的空窗一定落在「當地時間 ± 14 小時」的區間裡
    private static readonly TimeSpan OffsetBound = TimeSpan.FromHours(14);

    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>
    /// 嚴格晚於 <paramref name="after"/> 的下一次觸發時間；沒有時回 null。
    /// 一次性：<c>At</c> 晚於 <paramref name="after"/> 才回傳它。
    /// </summary>
    public static DateTimeOffset? NextOccurrence(Trigger trigger, string timeZoneId, DateTimeOffset after) => trigger switch
    {
        OneTimeTrigger oneTime => oneTime.At > after ? oneTime.At : null,
        RecurrenceTrigger recurrence => NextRecurrence(recurrence, ResolveTimeZone(timeZoneId), after),
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), "未知的觸發條件類型。"),
    };

    /// <summary>
    /// 接下來最多 <paramref name="count"/> 次觸發時間（都晚於 <paramref name="after"/>，由早到晚）。
    /// 搜尋範圍不超過 <paramref name="after"/> 之後的 <see cref="SearchLimitDays"/> 天。
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> NextOccurrences(Trigger trigger, string timeZoneId, DateTimeOffset after, int count)
    {
        var result = new List<DateTimeOffset>();
        var limit = after.AddDays(SearchLimitDays);
        var cursor = after;
        while (result.Count < count)
        {
            var next = NextOccurrence(trigger, timeZoneId, cursor);
            if (next is null || next > limit)
                break;
            result.Add(next.Value);
            cursor = next.Value;
        }
        return result;
    }

    /// <summary>
    /// 繁體中文描述，例如「每天 09:00」「每週一、五 09:00」「每月 15 日 09:00」「每平日 09:00」「2026-10-25 09:00」。
    /// 一次性的時間以項目時區的當地時間顯示。
    /// </summary>
    public static string Describe(Trigger trigger, string timeZoneId)
    {
        switch (trigger)
        {
            case OneTimeTrigger oneTime:
                var local = TimeZoneInfo.ConvertTime(oneTime.At, ResolveTimeZone(timeZoneId)).DateTime;
                return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            case RecurrenceTrigger recurrence:
                var time = recurrence.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
                return recurrence.Kind switch
                {
                    RecurrenceKind.Daily => $"每天 {time}",
                    RecurrenceKind.Weekdays => $"每平日 {time}",
                    RecurrenceKind.Weekly => DescribeWeekly(recurrence.Days, time),
                    RecurrenceKind.Monthly => $"每月 {recurrence.DayOfMonth ?? 1} 日 {time}",
                    _ => throw new ArgumentOutOfRangeException(nameof(trigger), "未知的週期種類。"),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(trigger), "未知的觸發條件類型。");
        }
    }

    /// <summary>
    /// 找得到的時區用它；空白或找不到（IANA id 不存在）時退回本機時區。
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static DateTimeOffset? NextRecurrence(RecurrenceTrigger trigger, TimeZoneInfo zone, DateTimeOffset after)
    {
        ValidateRecurrence(trigger);

        var localAfter = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var startDay = localAfter.Date;
        // 每週且沒指定星期：取 after 當地那天的星期（與計算時間點無關，只看 after）
        IReadOnlyList<DayOfWeek> weekly = trigger.Days is { Count: > 0 } days ? days : [localAfter.DayOfWeek];
        var dayOfMonth = trigger.DayOfMonth ?? 1;

        for (var offset = 0; offset <= SearchLimitDays; offset++)
        {
            var day = startDay.AddDays(offset);
            if (!Matches(trigger.Kind, day, weekly, dayOfMonth))
                continue;

            var instant = ToInstant(day.Add(trigger.TimeOfDay.ToTimeSpan()), zone);
            if (instant > after)
                return instant;
        }
        return null;
    }

    private static bool Matches(RecurrenceKind kind, DateTime day, IReadOnlyList<DayOfWeek> weekly, int dayOfMonth) => kind switch
    {
        RecurrenceKind.Daily => true,
        RecurrenceKind.Weekdays => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RecurrenceKind.Weekly => weekly.Contains(day.DayOfWeek),
        // 當月沒有那一天（例如 2 月的 30、31 日，或非閏年的 29 日）時取當月最後一天
        RecurrenceKind.Monthly => day.Day == Math.Min(dayOfMonth, DateTime.DaysInMonth(day.Year, day.Month)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "未知的週期種類。"),
    };

    private static void ValidateRecurrence(RecurrenceTrigger trigger)
    {
        if (trigger.Kind == RecurrenceKind.Monthly && trigger.DayOfMonth is { } day && day is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(trigger), "每月的日期必須介於 1 到 31 之間。");
    }

    /// <summary>把時區的當地時間換成絕對時間；空窗順延、重複取較早的那次。</summary>
    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            return FirstValidInstant(local, zone);

        if (zone.IsAmbiguousTime(local))
        {
            // 較早的那次是偏移量較大的那個（秋季後退前的夏令時間）
            var earlier = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, earlier);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// 空窗（不存在的當地時間）：空窗前後的偏移量不同。在 UTC 的 [當地時間 − 14 小時, 當地時間 + 14 小時] 區間內
    /// 二分找出偏移量變成新值的那一刻，就是空窗結束的瞬間（對應的當地時間是空窗的結束時間，例如 02:30 → 03:00）。
    /// </summary>
    private static DateTimeOffset FirstValidInstant(DateTime invalidLocal, TimeZoneInfo zone)
    {
        var asUtc = DateTime.SpecifyKind(invalidLocal, DateTimeKind.Utc);
        var low = new DateTimeOffset(asUtc - OffsetBound, TimeSpan.Zero);
        var high = new DateTimeOffset(asUtc + OffsetBound, TimeSpan.Zero);
        var afterGap = zone.GetUtcOffset(high);

        // 以分鐘為單位二分；時區的偏移量變化都落在整分鐘上
        while (high - low > TimeSpan.FromMinutes(1))
        {
            var middle = low.AddMinutes(Math.Floor((high - low).TotalMinutes / 2));
            if (zone.GetUtcOffset(middle) == afterGap)
                high = middle;
            else
                low = middle;
        }
        return high;
    }

    private static string DescribeWeekly(IReadOnlyList<DayOfWeek>? days, string time)
    {
        if (days is not { Count: > 0 })
            return $"每週 {time}";

        var names = MondayFirst
            .Where(day => days.Contains(day))
            .Select(ChineseDayName);
        return $"每週{string.Join("、", names)} {time}";
    }

    private static string ChineseDayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日",
    };
}
