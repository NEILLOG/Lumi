using System.Text.RegularExpressions;

namespace Lumi.Core.Notes;

/// <summary>設定的筆記資料夾解析結果。<paramref name="Problem"/> 有值表示設定不合法，已退回預設資料夾。</summary>
public sealed record NoteFolderResolution(string Folder, string? Problem);

/// <summary>
/// 筆記資料夾的設定規則（M10 10B）：空字串＝預設；非空先展開環境變數（<c>%VAR%</c>、<c>$VAR</c>、<c>${VAR}</c>），
/// 結果必須是完整路徑且不含非法字元，否則退回預設並說明原因（呼叫端決定要不要記 log）。
/// </summary>
public static partial class NoteFolderRules
{
    /// <summary>「文件」下的 <c>Lumi/notes</c>（沒有設定時）。</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lumi", "notes");

    /// <summary>解析設定值。不丟例外：任何不合法都退回預設。</summary>
    public static NoteFolderResolution Resolve(string? setting)
    {
        var raw = (setting ?? "").Trim();
        if (raw.Length == 0)
            return new NoteFolderResolution(DefaultFolder, null);

        var reason = Check(raw, out var full);
        return reason is null
            ? new NoteFolderResolution(full!, null)
            : new NoteFolderResolution(DefaultFolder, $"筆記資料夾設定不合法（{reason}），改用預設資料夾。");
    }

    /// <summary>儲存前的檢查：合法（含空字串）回 null，不合法回說明。</summary>
    public static string? Validate(string? setting)
    {
        var raw = (setting ?? "").Trim();
        if (raw.Length == 0)
            return null;
        var reason = Check(raw, out _);
        return reason is null ? null : $"筆記資料夾設定不合法（{reason}）。";
    }

    /// <summary>檢查非空的設定值：回傳不合法的原因（null 表示合法，<paramref name="fullPath"/> 是展開後的完整路徑）。</summary>
    private static string? Check(string raw, out string? fullPath)
    {
        fullPath = null;
        var expanded = ExpandVariables(raw, out var missing);
        if (missing is not null)
            return $"環境變數 {missing} 不存在";
        if (!Path.IsPathFullyQualified(expanded))
            return "筆記資料夾必須是完整路徑";
        if (expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || HasInvalidSegment(expanded))
            return "路徑含有不能用在檔案名稱的字元";

        fullPath = Path.GetFullPath(expanded);
        return null;
    }

    /// <summary>展開環境變數；變數不存在或為空時記下第一個名稱（回傳值無意義）。</summary>
    private static string ExpandVariables(string text, out string? missing)
    {
        string? firstMissing = null;

        string Lookup(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                firstMissing ??= name;
                return "";
            }
            return value;
        }

        var result = PercentVariable().Replace(text, m => Lookup(m.Groups[1].Value));
        result = BracedVariable().Replace(result, m => Lookup(m.Groups[1].Value));
        result = DollarVariable().Replace(result, m => Lookup(m.Groups[1].Value));
        missing = firstMissing;
        return result;
    }

    /// <summary>路徑的每一段（去掉根之後）都不能含非法的檔名字元；Windows 的磁碟機代號（C:）不算在內。</summary>
    private static bool HasInvalidSegment(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var rest = fullPath[root.Length..];
        var invalid = Path.GetInvalidFileNameChars();
        return rest.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.IndexOfAny(invalid) >= 0);
    }

    [GeneratedRegex(@"%([A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex PercentVariable();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex BracedVariable();

    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DollarVariable();
}
