using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Documents;

/// <summary>數字格式的種類：只決定顯示方式（日期、時間、百分比、經過時數或一般數字），不是完整的格式引擎。</summary>
internal enum ExcelNumberKind { Plain, Date, Time, Elapsed, Percent }

/// <summary>一個 cellXfs 樣式的數字種類；<see cref="HasSeconds"/> 只給經過時數（<c>[h]:mm:ss</c>）用。</summary>
internal sealed record ExcelNumberStyle(ExcelNumberKind Kind, bool HasSeconds = false)
{
    public static ExcelNumberStyle Plain { get; } = new(ExcelNumberKind.Plain);
}

/// <summary>
/// 依格式代碼判斷儲存格是不是日期、時間或百分比，並把序號還原成文字（M10 C4）。
/// 日期判斷：去掉引號文字、方括號區段（[Red]、[$-409]）與跳脫字元後，含 y、m、d、h、s 任一即視為日期；m 緊接在 h 後或緊接在 s 前是分鐘。
/// </summary>
internal static class ExcelNumberFormat
{
    private const double SecondsPerDay = 86_400;
    private const double MaxPlainMagnitude = 1e15;
    private const double MinPlainMagnitude = 1e-10;

    /// <summary>內建格式代碼（ECMA-376 的 numFmtId）的代表性寫法；只用來判斷種類。</summary>
    private static readonly Dictionary<int, string> BuiltinCodes = new()
    {
        [14] = "m/d/yyyy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
        [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "m/d/yyyy h:mm",
        [27] = "yyyy年m月", [28] = "m月d日", [29] = "m月d日", [30] = "m/d/yy", [31] = "yyyy年m月d日",
        [32] = "h时mm分", [33] = "h时mm分ss秒", [34] = "上午/下午h时mm分", [35] = "上午/下午h时mm分ss秒", [36] = "yyyy年m月d日",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mm:ss.0",
        [50] = "yyyy年m月", [51] = "m月d日", [52] = "上午/下午h时mm分", [53] = "上午/下午h時mm分ss秒", [54] = "m月d日",
        [55] = "上午/下午h时mm分", [56] = "上午/下午h时mm分ss秒", [57] = "yyyy年m月", [58] = "m月d日",
    };

    private static readonly Regex AmPmPattern = new("AM/PM|A/P", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>依 numFmtId 取得樣式：自訂格式看 <paramref name="custom"/>，否則用內建代碼；內建 9、10 是百分比。</summary>
    public static ExcelNumberStyle ForFormatId(int numFmtId, IReadOnlyDictionary<int, string> custom)
    {
        if (custom.TryGetValue(numFmtId, out var code))
            return Classify(code);
        if (numFmtId is 9 or 10)
            return new ExcelNumberStyle(ExcelNumberKind.Percent);
        return BuiltinCodes.TryGetValue(numFmtId, out var builtin) ? Classify(builtin) : ExcelNumberStyle.Plain;
    }

    /// <summary>判斷一段格式代碼的種類。</summary>
    public static ExcelNumberStyle Classify(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return ExcelNumberStyle.Plain;

        var literal = new StringBuilder();
        var elapsedHours = false;
        for (var i = 0; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '"':
                    var close = code.IndexOf('"', i + 1);
                    i = close < 0 ? code.Length : close;
                    break;
                case '\\' or '_' or '*':
                    i++;   // 跳脫的下一個字元（\x、_x 留白、*x 填滿）
                    break;
                case '[':
                    var end = code.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        i = code.Length;
                        break;
                    }
                    var inner = code[(i + 1)..end];
                    if (inner.Length > 0 && inner.All(c => c is 'h' or 'H'))
                        elapsedHours = true;
                    i = end;
                    break;
                default:
                    literal.Append(code[i]);
                    break;
            }
        }

        var text = AmPmPattern.Replace(literal.ToString(), "");
        var tokens = new List<char>();
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            if (lower is 'y' or 'm' or 'd' or 'h' or 's')
                tokens.Add(lower);
        }

        // 連續相同的字母成一段（mm、hh…）；m 段緊接在 h 段後面，或後面接 s 段，就是分鐘
        var hasDate = false;
        var hasTime = false;
        for (var i = 0; i < tokens.Count;)
        {
            var end = i;
            while (end < tokens.Count && tokens[end] == tokens[i])
                end++;
            switch (tokens[i])
            {
                case 'y' or 'd':
                    hasDate = true;
                    break;
                case 'h' or 's':
                    hasTime = true;
                    break;
                case 'm':
                    var isMinute = (i > 0 && tokens[i - 1] == 'h') || (end < tokens.Count && tokens[end] == 's');
                    if (isMinute) hasTime = true;
                    else hasDate = true;
                    break;
            }
            i = end;
        }
        var hasSeconds = tokens.Contains('s');

        if (elapsedHours)
            return new ExcelNumberStyle(ExcelNumberKind.Elapsed, hasSeconds);
        if (hasDate)
            return new ExcelNumberStyle(ExcelNumberKind.Date);
        if (hasTime)
            return new ExcelNumberStyle(ExcelNumberKind.Time);
        if (text.Contains('%'))
            return new ExcelNumberStyle(ExcelNumberKind.Percent);
        return ExcelNumberStyle.Plain;
    }

    /// <summary>
    /// 把儲存格的數值文字（<c>&lt;v&gt;</c>）依樣式還原。無法解析或超出日期範圍時原樣回傳。
    /// </summary>
    public static string Format(string raw, ExcelNumberStyle style, bool date1904)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            return raw;

        return style.Kind switch
        {
            ExcelNumberKind.Percent => PlainNumber(value * 100) + "%",
            ExcelNumberKind.Date => FormatDate(value, date1904) ?? PlainNumber(value),
            ExcelNumberKind.Time => FormatTime(value) ?? PlainNumber(value),
            ExcelNumberKind.Elapsed => FormatElapsed(value, style.HasSeconds) ?? PlainNumber(value),
            _ => PlainNumber(value),
        };
    }

    /// <summary>
    /// 日期：整數部分是日期；有時間部分（秒數不為 0）時加上 <c>HH:mm:ss</c>，否則只顯示 <c>yyyy-MM-dd</c>。
    /// 1900 系統的序號 60 是 Excel 的閏年錯誤（1900-02-29 不存在），照 Excel 的顯示保留。
    /// </summary>
    private static string? FormatDate(double serial, bool date1904)
    {
        if (serial < 0)
            return null;

        var total = (long)Math.Round(serial * SecondsPerDay, MidpointRounding.AwayFromZero);
        var date = DateText(total / (long)SecondsPerDay, date1904);
        if (date is null)
            return null;

        var seconds = total % (long)SecondsPerDay;
        return seconds == 0 ? date : date + " " + TimeOfDay(seconds);
    }

    /// <summary>只有時間的格式：取小數部分，<c>HH:mm:ss</c>。</summary>
    private static string? FormatTime(double serial)
    {
        if (serial < 0)
            return null;
        var total = (long)Math.Round(serial * SecondsPerDay, MidpointRounding.AwayFromZero);
        return TimeOfDay(total % (long)SecondsPerDay);
    }

    /// <summary>經過時數（<c>[h]:mm</c>）：可以超過 24 小時。</summary>
    private static string? FormatElapsed(double serial, bool hasSeconds)
    {
        if (serial < 0)
            return null;
        var total = (long)Math.Round(serial * SecondsPerDay, MidpointRounding.AwayFromZero);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        var text = FormattableString.Invariant($"{hours}:{minutes:00}");
        return hasSeconds ? text + FormattableString.Invariant($":{seconds:00}") : text;
    }

    private static string? DateText(long days, bool date1904)
    {
        if (date1904)
        {
            var epoch1904 = new DateTime(1904, 1, 1);
            return days > (DateTime.MaxValue - epoch1904).TotalDays ? null : epoch1904.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (days < 1)
            return null;   // 0 在 Excel 是 1900-01-00，這種值保留原始數字
        if (days == 60)
            return "1900-02-29";

        // 1900 系統：序號 1–59 從 1899-12-31 起算；60 之後多算了一天的閏年錯誤，改從 1899-12-30 起算
        var epoch = days < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
        return days > (DateTime.MaxValue - epoch).TotalDays ? null : epoch.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string TimeOfDay(long seconds) =>
        FormattableString.Invariant($"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}");

    /// <summary>
    /// 一般數字：用 15 位有效數字輸出（去掉 0.1+0.2 那種浮點尾巴），整數不加 <c>.0</c>。
    /// 極大或極小的數字保留科學記號，避免 decimal 轉換失真。
    /// </summary>
    private static string PlainNumber(double value)
    {
        if (value == 0)
            return "0";

        var text = value.ToString("G15", CultureInfo.InvariantCulture);
        var magnitude = Math.Abs(value);
        if (magnitude >= MinPlainMagnitude && magnitude < MaxPlainMagnitude
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
            return exact.ToString(CultureInfo.InvariantCulture);
        return text;
    }
}
