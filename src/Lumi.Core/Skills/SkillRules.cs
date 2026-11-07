using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Core.Skills;

/// <summary>技能的驗證規則與上限（M29 5.2，純函式）。store、工具、設定頁與 prompt 共用，只在這裡定義一次。</summary>
public static partial class SkillRules
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxBodyLength = 20_000;
    public const long MaxFileBytes = 100 * 1024;
    public const long MaxSkillBytes = 2 * 1024 * 1024;
    public const int MaxFiles = 50;
    public const long MaxZipBytes = 10 * 1024 * 1024;
    public const int MaxEnabled = 30;

    /// <summary>技能的主檔名。</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>只認這些副檔名的文字檔（D5），不分大小寫。</summary>
    public static IReadOnlySet<string> TextExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".csv", ".tsv", ".json", ".yaml", ".yml", ".xml", ".html", ".htm",
    };

    /// <summary>名稱比較一律用這個。</summary>
    public static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>去頭尾空白。null 當空字串。</summary>
    public static string NormalizeName(string? raw) => (raw ?? "").Trim();

    /// <summary>換行與連續空白壓成一個空格、去頭尾空白（描述存成單行）。</summary>
    public static string NormalizeDescription(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : Whitespace().Replace(raw.Trim(), " ");

    /// <summary>內文統一用 <c>\n</c> 換行、去掉頭尾的空行。</summary>
    public static string NormalizeBody(string? raw) =>
        (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ', '\t');

    /// <summary>字數：以字形叢集計（中文、emoji 各算一字），和記憶相同。</summary>
    public static int Length(string? text) => string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>檢查<b>已正規化</b>的名稱；通過回 null，否則回可以直接顯示的原因。名稱同時是資料夾名稱，所以要能在 Windows 與 macOS 建立。</summary>
    public static string? ValidateName(string name)
    {
        if (name.Length == 0)
            return "請輸入技能名稱。";
        if (Length(name) > MaxNameLength)
            return $"技能名稱最多 {MaxNameLength} 字。";
        if (name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            return "技能名稱不能有 / \\ : * ? \" < > | 這些符號。";
        if (name.StartsWith('.') || name.EndsWith('.') || name != name.Trim())
            return "技能名稱的開頭和結尾不能是句點或空白。";
        if (ReservedNames.Contains(name.Split('.')[0]))
            return "這個名稱是系統保留字，請換一個。";
        return null;
    }

    /// <summary>檢查<b>已正規化</b>的描述（「什麼時候用」）。</summary>
    public static string? ValidateDescription(string description)
    {
        if (description.Length == 0)
            return "請說明什麼時候要用這個技能。";
        if (Length(description) > MaxDescriptionLength)
            return $"「什麼時候用」最多 {MaxDescriptionLength} 字。";
        return null;
    }

    /// <summary>檢查<b>已正規化</b>的內文（步驟）。</summary>
    public static string? ValidateBody(string body)
    {
        if (body.Length == 0)
            return "請寫下這個技能的步驟。";
        if (Length(body) > MaxBodyLength)
            return $"步驟最多 {MaxBodyLength:N0} 字。";
        return null;
    }

    /// <summary>名稱、描述、內文一起檢查（先正規化），回傳第一個問題；都通過回 null。</summary>
    public static string? Validate(SkillDraft draft) =>
        ValidateName(NormalizeName(draft.Name))
        ?? ValidateDescription(NormalizeDescription(draft.Description))
        ?? ValidateBody(NormalizeBody(draft.Body));

    /// <summary>附帶檔案是不是 Lumi 認得的文字檔（只看副檔名）。</summary>
    public static bool IsTextFile(string path) => TextExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 相對路徑是否安全：不是絕對路徑、沒有 <c>..</c>、沒有空的段落，也不在 <c>scripts/</c> 底下。
    /// 分隔符號 <c>/</c> 與 <c>\</c> 都接受；回傳 true 時 <paramref name="normalized"/> 是用 <c>/</c> 分隔的版本。
    /// </summary>
    public static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var parts = path.Trim().Replace('\\', '/').Split('/');
        if (parts.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path) || path.Contains(':'))
            return false;
        if (parts.Any(p => p.Length == 0 || p is "." or ".."))
            return false;
        if (string.Equals(parts[0], "scripts", StringComparison.OrdinalIgnoreCase))
            return false;
        normalized = string.Join('/', parts);
        return true;
    }

    /// <summary>
    /// 跳脫結構標籤：<c>&lt;lumi_skill</c>、<c>&lt;/lumi_skill</c>、<c>&lt;lumi_skills</c>、<c>&lt;/lumi_skills</c>（不分大小寫）改成實體，
    /// 技能內容或名稱不能提早關閉區塊。做法同 <c>MemoryPromptLayer.EscapeTags</c>。
    /// </summary>
    public static string EscapeTags(string text) => TagPattern().Replace(text, "&lt;$1$2");

    /// <summary>清單估計佔用的字元數（設定頁換算 token 用）：每個可用技能一行「- 名稱：描述」。</summary>
    public static int ListCharacters(IEnumerable<SkillInfo> skills) =>
        skills.Where(s => s.IsUsable).Sum(s => s.Name.Length + s.Description.Length + 4);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("<(/?)(lumi_skills?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
