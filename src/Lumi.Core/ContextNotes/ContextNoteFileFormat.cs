using System.Text.RegularExpressions;

namespace Lumi.Core.ContextNotes;

/// <summary>note.md 的格式（計畫 4.2）：開頭三行（標題、空行、目標）由程式產生，後面是版本的 content。</summary>
public static class ContextNoteFileFormat
{
    public const string GoalPrefix = "**目標**：";

    private static readonly Regex LineBreaks = new(@"\r\n|\r|\n", RegexOptions.CultureInvariant);

    /// <summary>組出 note.md 的文字。標題與目標裡的換行換成空白，免得開頭三行的格式跑掉。</summary>
    public static string Compose(string title, string goal, string content) =>
        $"# {OneLine(title)}\n\n{GoalPrefix}{OneLine(goal)}\n\n{content}";

    /// <summary>
    /// 讀 note.md：內容從第一個認得的 <c>## </c> 標題開始，之前是檔頭（找目標行）。
    /// 目標空白或超過 <see cref="ContextNoteLimits.GoalMaxLength"/> 字，或檔頭沒有目標行，就用 <paramref name="fallbackGoal"/>。
    /// 回傳的內容一律是 <see cref="ContextNoteDocument.Parse"/> 再 <see cref="ContextNoteDocument.ToMarkdown"/> 的結果。
    /// </summary>
    public static (string Goal, string Content) Parse(string fileText, string fallbackGoal)
    {
        var lines = LineBreaks.Replace(fileText ?? "", "\n").Split('\n');
        var contentStart = FindContentStart(lines);

        var goalLine = lines.Take(contentStart).FirstOrDefault(l => l.TrimStart().StartsWith(GoalPrefix, StringComparison.Ordinal));
        var goal = goalLine?.TrimStart()[GoalPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(goal) || goal.Length > ContextNoteLimits.GoalMaxLength)
            goal = fallbackGoal;

        var content = ContextNoteDocument.Parse(string.Join('\n', lines[contentStart..])).ToMarkdown();
        return (goal, content);
    }

    private static string OneLine(string text) => LineBreaks.Replace(text, " ");

    /// <summary>內容開始的行號。有認得的標題就用它；沒有時跳過開頭的空行、`# ` 標題行與目標行，剩下的都是內容。</summary>
    private static int FindContentStart(string[] lines)
    {
        var heading = Array.FindIndex(lines, IsKnownHeading);
        if (heading >= 0)
            return heading;

        var i = SkipBlank(lines, 0);
        if (i < lines.Length && lines[i].StartsWith("# ", StringComparison.Ordinal))
            i = SkipBlank(lines, i + 1);
        if (i < lines.Length && lines[i].TrimStart().StartsWith(GoalPrefix, StringComparison.Ordinal))
            i++;
        return i;
    }

    private static bool IsKnownHeading(string line) =>
        ContextNoteDocument.SectionNames.Any(name => string.Equals(line, "## " + name, StringComparison.Ordinal));

    private static int SkipBlank(string[] lines, int from)
    {
        var i = from;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
            i++;
        return i;
    }
}
