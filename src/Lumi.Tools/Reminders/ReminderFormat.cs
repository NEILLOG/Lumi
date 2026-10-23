using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumi.Core.Reminders;

namespace Lumi.Tools.Reminders;

/// <summary>提醒工具共用的格式：星期、給使用者看的時間、ISO 8601 輸出、時區解析與 JSON 設定（中文不跳脫）。</summary>
internal static class ReminderFormat
{
    /// <summary>工具回傳的 JSON：欄位 snake_case、null 不輸出、中文照字面輸出（模型讀得懂）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] ChineseDays = ["週日", "週一", "週二", "週三", "週四", "週五", "週六"];

    public static string DayName(DayOfWeek day) => ChineseDays[(int)day];

    /// <summary>給使用者看的完整時間：「2026-10-25（週日）09:00」，以 <paramref name="zone"/> 的當地時間。</summary>
    public static string Local(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return $"{local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}（{DayName(local.DayOfWeek)}）{local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>摘要用的短格式：「10/25（週日）09:00」。</summary>
    public static string Short(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return $"{local.ToString("MM/dd", CultureInfo.InvariantCulture)}（{DayName(local.DayOfWeek)}）{local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>只有時分：「09:15」。</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>ISO 8601（"O" 格式），偏移量是 <paramref name="zone"/> 在該時刻的偏移。</summary>
    public static string? Iso(DateTimeOffset? at, TimeZoneInfo zone) =>
        at is { } value ? TimeZoneInfo.ConvertTime(value, zone).ToString("O", CultureInfo.InvariantCulture) : null;

    /// <summary>秒數轉成給人看的時長：「10 分鐘」「1 小時 30 分鐘」「45 秒」。</summary>
    public static string Duration(int seconds)
    {
        var parts = new List<string>();
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var rest = seconds % 60;
        if (hours > 0) parts.Add($"{hours} 小時");
        if (minutes > 0) parts.Add($"{minutes} 分鐘");
        if (rest > 0) parts.Add($"{rest} 秒");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// 時區名稱解析：空白時用本機時區；找不到的 IANA 名稱回錯誤（不默默改用本機，免得排在錯的時區）。
    /// </summary>
    public static bool TryResolveZone(string? raw, out TimeZoneInfo zone, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            zone = TimeZoneInfo.Local;
            error = null;
            return true;
        }

        var name = raw.Trim();
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(name);
            error = null;
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Local;
            error = $"不認得的時區：{name}。請使用 IANA 名稱，例如 Asia/Taipei。";
            return false;
        }
    }

    /// <summary>項目儲存的時區 id 還原成時區（找不到時 Core 會退回本機）。</summary>
    public static TimeZoneInfo ZoneOf(string timeZoneId) => ScheduleCalculator.ResolveTimeZone(timeZoneId);

    /// <summary>必填文字：去頭尾空白後不可為空，且不超過上限。</summary>
    public static bool TryCheckText(string? raw, string name, int maxChars, out string text, out string? error)
    {
        text = raw?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = $"{name} 不可為空。";
            return false;
        }
        if (text.Length > maxChars)
        {
            error = $"{name} 太長（上限 {maxChars} 字），請縮短後再試。";
            return false;
        }
        error = null;
        return true;
    }
}
