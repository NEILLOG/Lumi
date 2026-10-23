using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Core.Memory;

/// <summary>記憶的驗證與格式規則（純函式）。store、工具與設定頁共用，只在這裡定義一次。</summary>
public static partial class MemoryRules
{
    /// <summary>換行與連續空白壓成一個空格、去頭尾空白。null 當空字串。</summary>
    public static string NormalizeContent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>字數：以字形叢集計（中文、emoji 各算一字）。</summary>
    public static int Length(string? text) => string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>檢查<b>已正規化</b>的內容；通過回 null，否則回可以直接顯示的原因。<paramref name="checkSecret"/> 為 false 時略過機密樣式檢查（使用者親手輸入）。</summary>
    public static string? ValidateContent(string normalized, MemoryOptions options, bool checkSecret = true)
    {
        if (normalized.Length == 0)
            return "記憶的內容不可為空。";
        var length = Length(normalized);
        if (length > options.MaxContentLength)
            return $"這則記憶太長（{length} 字，上限 {options.MaxContentLength} 字），請縮成一句話。";
        if (checkSecret && LooksLikeSecret(normalized))
            return "這看起來像是金鑰、密碼或卡號，不會記進長期記憶。請不要把機密放進記憶。";
        return null;
    }

    /// <summary>
    /// 最小的機密樣式檢查：<c>sk-…</c>、<c>Bearer …</c>、32 字元以上的連續英數字串、13～19 位的連續數字（可含空格或連字號）。
    /// 不是完整的機密偵測，只擋最常見的失誤（模型把使用者貼的金鑰順手記下來）。
    /// </summary>
    public static bool LooksLikeSecret(string text) =>
        !string.IsNullOrEmpty(text)
        && (SkKeyPattern().IsMatch(text) || BearerPattern().IsMatch(text) || LongTokenPattern().IsMatch(text) || CardNumberPattern().IsMatch(text));

    /// <summary>比對用的指紋：正規化後轉小寫。相同指紋視為同一則記憶。</summary>
    public static string Fingerprint(string? content) => NormalizeContent(content).ToLowerInvariant();

    public static string FormatId(int id) => $"m{id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>接受 <c>m3</c>、<c>M3</c>、<c>3</c>（前後空白可）；必須是正整數。</summary>
    public static bool TryParseId(string? text, out int id)
    {
        id = 0;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
            return false;
        if (s[0] is 'm' or 'M')
            s = s[1..];
        return s.Length > 0 && s.All(char.IsAsciiDigit)
            && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>接受 <c>profile</c>／<c>preference</c>／<c>work</c>／<c>project</c>（不分大小寫、前後空白可）；不接受數字。</summary>
    public static bool TryParseCategory(string? text, out MemoryCategory category)
    {
        category = default;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || char.IsDigit(s[0]) || s.Contains(','))
            return false;   // Enum.TryParse 會接受數字與逗號組合（"profile, work"），這裡只要單一名稱
        return Enum.TryParse(s, ignoreCase: true, out category) && Enum.IsDefined(category);
    }

    /// <summary>只接受 <c>yyyy-MM-dd</c>。</summary>
    public static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>給模型的分類代號（小寫英文）。</summary>
    public static string CategoryKey(MemoryCategory category) => category switch
    {
        MemoryCategory.Profile => "profile",
        MemoryCategory.Preference => "preference",
        MemoryCategory.Work => "work",
        _ => "project",
    };

    /// <summary>給人看的分類名稱：本人／偏好／工作／專案。</summary>
    public static string CategoryName(MemoryCategory category) => category switch
    {
        MemoryCategory.Profile => "本人",
        MemoryCategory.Preference => "偏好",
        MemoryCategory.Work => "工作",
        _ => "專案",
    };

    /// <summary>有效＝沒有到期日，或到期日不早於今天。</summary>
    public static bool IsActive(MemoryItem item, DateOnly today) => item.ExpiresAt is null || item.ExpiresAt.Value >= today;

    /// <summary>使用者的訊息有沒有含任何觸發詞（不分大小寫、子字串比對）。</summary>
    public static bool ContainsTriggerWord(string? userText, IEnumerable<string> triggerWords)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;
        return triggerWords.Any(w => !string.IsNullOrWhiteSpace(w) && userText.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex SkKeyPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._\-]{16,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9]{32,}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LongTokenPattern();

    [GeneratedRegex(@"(?<!\d)\d(?:[ \-]?\d){12,18}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CardNumberPattern();
}
