using System.Globalization;
using Lumi.Core.Tools;

namespace Lumi.Tools.Notes;

/// <summary>筆記工具共用的日期與時間（都以使用者電腦的本機時間計算，時間來源是注入的 <see cref="TimeProvider"/>）。</summary>
internal static class NoteDates
{
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>目前的本機時間。</summary>
    public static DateTimeOffset LocalNow(ToolContext context) =>
        TimeZoneInfo.ConvertTime(context.Time.GetUtcNow(), context.Time.LocalTimeZone);

    public static DateOnly Today(ToolContext context) => DateOnly.FromDateTime(LocalNow(context).DateTime);

    /// <summary>
    /// 解析 <c>yyyy-MM-dd</c>。空白或省略回傳 <paramref name="fallback"/>。
    /// 格式錯誤時 <paramref name="error"/> 是 <c>{name} 格式必須是 yyyy-MM-dd（例如 2026-09-30）。</c>。
    /// </summary>
    public static bool TryParse(string? text, string name, DateOnly fallback, out DateOnly date, out string? error)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            date = fallback;
            error = null;
            return true;
        }
        if (!DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            error = $"{name} 格式必須是 yyyy-MM-dd（例如 2026-09-30）。";
            return false;
        }
        error = null;
        return true;
    }

    public static string Format(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);
}
