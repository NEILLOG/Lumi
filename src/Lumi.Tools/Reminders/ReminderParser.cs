using System.Globalization;
using System.Text.RegularExpressions;
using Lumi.Core.Reminders;

namespace Lumi.Tools.Reminders;

/// <summary>
/// <c>create_reminder</c> 的觸發條件解析（一次性的 <c>at</c>，或週期的 <c>recurrence</c>＋<c>time</c>）。
/// 錯誤訊息沿用舊版（tool.md 5.3.7）的語意，參數名稱改成 snake_case。確認卡片與實際建立共用這段，兩邊結果一致。
/// </summary>
internal static partial class ReminderParser
{
    /// <summary>不含偏移的當地時間格式（<c>2026-10-25T09:00</c> 或 <c>2026-10-25 09:00</c>，可帶秒）。</summary>
    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
    ];

    /// <summary>春季前進的空窗最長要往後找幾分鐘（實際的時區只有 1 小時，這裡留足餘量）。</summary>
    private const int MaxGapMinutes = 24 * 60;

    public const string AtFormatError = "時間格式無效，請使用含時區偏移的 ISO 8601 格式（例如 2026-10-05T15:30:00+08:00），或不含偏移的當地時間（例如 2026-10-25T09:00）。";

    public static bool TryParseTrigger(CreateReminderArgs args, TimeZoneInfo zone, out Trigger? trigger, out string? error)
    {
        var hasAt = !string.IsNullOrWhiteSpace(args.At);
        var hasRecurrence = !string.IsNullOrWhiteSpace(args.Recurrence);
        if (hasAt == hasRecurrence)
        {
            trigger = null;
            error = hasAt
                ? "at 與 recurrence 只能擇一：一次性提醒用 at，週期提醒用 recurrence 加 time。"
                : "請指定提醒時間：一次性提醒用 at，週期提醒用 recurrence 加 time（兩者只能擇一）。";
            return false;
        }

        return hasAt
            ? TryParseOneTime(args.At!, zone, out trigger, out error)
            : TryParseRecurrence(args, out trigger, out error);
    }

    private static bool TryParseOneTime(string text, TimeZoneInfo zone, out Trigger? trigger, out string? error)
    {
        var value = text.Trim();
        if (OffsetSuffix().IsMatch(value))
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                trigger = new OneTimeTrigger(at);
                error = null;
                return true;
            }
        }
        else if (DateTime.TryParseExact(value, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            trigger = new OneTimeTrigger(LocalToInstant(local, zone));
            error = null;
            return true;
        }

        trigger = null;
        error = AtFormatError;
        return false;
    }

    /// <summary>
    /// 當地時間換成絕對時間。空窗（春季前進，時間不存在）順延到第一個有效時間；重複（秋季後退）取較早的那次。
    /// </summary>
    internal static DateTimeOffset LocalToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; zone.IsInvalidTime(local) && i < MaxGapMinutes; i++)
            local = local.AddMinutes(1);

        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max());
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static bool TryParseRecurrence(CreateReminderArgs args, out Trigger? trigger, out string? error)
    {
        trigger = null;
        var kind = args.Recurrence!.Trim().ToLowerInvariant() switch
        {
            "daily" => RecurrenceKind.Daily,
            "weekdays" => RecurrenceKind.Weekdays,
            "weekly" => RecurrenceKind.Weekly,
            "monthly" => RecurrenceKind.Monthly,
            _ => (RecurrenceKind?)null,
        };
        if (kind is null)
        {
            error = "未知的 recurrence 類型，僅支援 daily, weekdays, weekly, monthly。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(args.Time))
        {
            error = "缺少必填參數 time (格式必須為 HH:mm，例如 09:30)。";
            return false;
        }
        var timeText = args.Time.Trim();
        if (!ClockText().IsMatch(timeText) || !TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            error = "時間格式無效，請使用 HH:mm 格式 (例如 09:00 或 18:30)。";
            return false;
        }

        IReadOnlyList<DayOfWeek>? days = null;
        if (kind == RecurrenceKind.Weekly)
        {
            days = ParseDays(args.Days);
            if (days.Count == 0)
            {
                error = "每週排程 (weekly) 必須指定至少一個有效的星期幾 (參數 days，例如 [\"monday\", \"friday\"] 或 [\"1\", \"5\"])。";
                return false;
            }
        }

        int? dayOfMonth = null;
        if (kind == RecurrenceKind.Monthly)
        {
            var day = args.DayOfMonth ?? 1;
            if (day is < 1 or > 31)
            {
                error = "每月排程的 day_of_month 必須介於 1 到 31 之間。";
                return false;
            }
            dayOfMonth = day;
        }

        trigger = new RecurrenceTrigger(kind.Value, time, days, dayOfMonth);
        error = null;
        return true;
    }

    /// <summary>
    /// 星期：英文名（不分大小寫）或數字（1＝週一…7＝週日，0 也是週日）。無法辨識的項目略過；去重並依週一開始排序。
    /// </summary>
    internal static IReadOnlyList<DayOfWeek> ParseDays(IEnumerable<string>? raw)
    {
        if (raw is null)
            return [];

        var days = new HashSet<DayOfWeek>();
        foreach (var text in raw)
        {
            var day = ParseDay(text);
            if (day is { } value)
                days.Add(value);
        }
        return days.OrderBy(d => ((int)d + 6) % 7).ToList();
    }

    private static DayOfWeek? ParseDay(string text) => text.Trim().ToLowerInvariant() switch
    {
        "monday" or "1" => DayOfWeek.Monday,
        "tuesday" or "2" => DayOfWeek.Tuesday,
        "wednesday" or "3" => DayOfWeek.Wednesday,
        "thursday" or "4" => DayOfWeek.Thursday,
        "friday" or "5" => DayOfWeek.Friday,
        "saturday" or "6" => DayOfWeek.Saturday,
        "sunday" or "7" or "0" => DayOfWeek.Sunday,
        _ => null,
    };

    [GeneratedRegex(@"\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetSuffix();

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex ClockText();
}
