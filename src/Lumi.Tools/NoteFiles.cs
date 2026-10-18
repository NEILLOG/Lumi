using System.Text.RegularExpressions;

namespace Lumi.Tools;

/// <summary>示範筆記工具共用的檔名規則：只接受簡單檔名（不含路徑），且最後的完整路徑一定在筆記資料夾內。</summary>
internal static partial class NoteFiles
{
    public const int MaxContentChars = 100_000;

    [GeneratedRegex(@"^[\p{L}\p{N}_\- ]{1,80}(\.(txt|md))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    /// <summary>檢查檔名；沒有副檔名就補 <c>.md</c>。不合法回 null。</summary>
    public static string? Normalize(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !ValidName().IsMatch(trimmed) || trimmed.StartsWith(' ') || trimmed.EndsWith(' '))
            return null;
        var ext = Path.GetExtension(trimmed);
        return ext.Length == 0 ? trimmed + ".md" : trimmed;
    }

    public const string InvalidNameMessage =
        "檔名不合法：只能是簡單的名稱（中英文、數字、空白、底線、連字號，最長 80 字），副檔名只能是 .txt 或 .md，不能包含資料夾路徑。";

    /// <summary>檔名對應的完整路徑；結果不在筆記資料夾內就回 null（雙重防護）。</summary>
    public static string? ResolvePath(string notesDirectory, string normalizedName)
    {
        var root = Path.GetFullPath(notesDirectory);
        var full = Path.GetFullPath(Path.Combine(root, normalizedName));
        return string.Equals(Path.GetDirectoryName(full), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal) ? full : null;
    }
}
