using System.Text.RegularExpressions;

namespace Lumi.Core.ContextNotes;

/// <param name="NoChange">書記回 <c>&lt;no_change/&gt;</c>。</param>
/// <param name="Note">書記回的 <c>&lt;note&gt;</c> 內容（去頭尾空白）。</param>
public sealed record ScribeOutput(bool NoChange, string? Note, string? GoalHint);

public static class ScribeOutputParser
{
    private const string NoteOpen = "<note>", NoteClose = "</note>";
    private const string HintOpen = "<goal_hint>", HintClose = "</goal_hint>";

    /// <summary>三種寫法都算：<c>&lt;no_change/&gt;</c>、<c>&lt;no_change /&gt;</c>、<c>&lt;no_change&gt;&lt;/no_change&gt;</c>。</summary>
    private static readonly Regex NoChangeTag = new(@"<no_change\s*/>|<no_change\s*>\s*</no_change\s*>", RegexOptions.CultureInvariant);

    /// <returns>格式不對（沒有完整的 <c>&lt;note&gt;</c>，也沒有 no_change）回 null。</returns>
    public static ScribeOutput? Parse(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var note = ExtractNote(answer);
        var noChange = NoChangeTag.IsMatch(answer);
        if (note is null && !noChange)
            return null;
        // 兩者都有時以 <note> 為準（NoChange = false）
        return new ScribeOutput(NoChange: note is null, Note: note, GoalHint: ExtractGoalHint(answer));
    }

    /// <summary>第一個 <c>&lt;note&gt;</c> 到最後一個 <c>&lt;/note&gt;</c> 之間。</summary>
    private static string? ExtractNote(string answer)
    {
        var start = answer.IndexOf(NoteOpen, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var contentStart = start + NoteOpen.Length;
        var end = answer.LastIndexOf(NoteClose, StringComparison.Ordinal);
        if (end < contentStart)
            return null;
        var inner = answer[contentStart..end].Trim();
        return inner.Length == 0 ? null : inner;
    }

    private static string? ExtractGoalHint(string answer)
    {
        var start = answer.IndexOf(HintOpen, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var contentStart = start + HintOpen.Length;
        var end = answer.IndexOf(HintClose, contentStart, StringComparison.Ordinal);
        if (end < 0)
            return null;
        var hint = answer[contentStart..end].Trim();
        if (hint.Length == 0)
            return null;
        return hint.Length > ContextNoteLimits.GoalHintMaxLength ? hint[..ContextNoteLimits.GoalHintMaxLength] : hint;
    }
}
