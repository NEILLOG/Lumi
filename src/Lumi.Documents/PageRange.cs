using System.Globalization;

namespace Lumi.Documents;

/// <summary>解析 PDF 頁碼與 PowerPoint 張數的選擇器：<c>3</c> 或 <c>3-5</c>（1 起算，允許頭尾與連字號旁的空白）。</summary>
internal static class PageRange
{
    /// <summary>一次最多讀取的頁數或張數。</summary>
    public const int MaxSpan = 50;

    /// <param name="text">選擇器文字；null 或空白回錯誤。</param>
    /// <param name="max">文件的頁數或張數。</param>
    /// <param name="range">解析結果（From ≤ To，都在 1 到 max 之間）。</param>
    /// <param name="error">失敗時的固定繁中訊息。</param>
    public static bool TryParse(string? text, int max, out (int From, int To) range, out string error)
    {
        range = default;
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "請指定要讀的頁碼或張數，例如 3 或 3-5。";
            return false;
        }

        var parts = text.Trim().Split('-');
        if (parts.Length > 2 || !TryParseNumber(parts[0], out var from) || (parts.Length == 2 && !TryParseNumber(parts[1], out _)))
        {
            error = "頁碼格式不正確，請用 3 或 3-5 的形式。";
            return false;
        }

        var to = parts.Length == 2 ? int.Parse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture) : from;

        if (from < 1)
        {
            error = "頁碼或張數從 1 開始。";
            return false;
        }
        if (to < from)
        {
            error = "結束的頁碼或張數不能小於開始的頁碼或張數。";
            return false;
        }
        if (from > max)
        {
            error = $"超出範圍：文件只有 {max} 頁或張。";
            return false;
        }

        to = Math.Min(to, max);
        if (to - from + 1 > MaxSpan)
        {
            error = $"一次最多讀取 {MaxSpan} 頁或張，請縮小範圍。";
            return false;
        }

        range = (from, to);
        return true;
    }

    private static bool TryParseNumber(string part, out int value)
    {
        var trimmed = part.Trim();
        // NumberStyles.None：只接受純數字（不接受正負號、千分位、小數）；溢位時 TryParse 也會失敗
        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
