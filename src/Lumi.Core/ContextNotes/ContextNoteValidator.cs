using System.Text.RegularExpressions;

namespace Lumi.Core.ContextNotes;

public enum NoteRejectReason { None, WrongStructure, TooLong, TooManyChanges }

/// <summary>書記輸出的程式端檢查（計畫 4.6）。</summary>
public static class ContextNoteValidator
{
    private static readonly Regex WholePlaceholder = new(@"^\[\[保護:[0-9]+\]\]$", RegexOptions.CultureInvariant);

    /// <param name="maskedBefore">送給書記的佔位符版研究本。</param>
    /// <param name="scribeMarkdown">書記回來的 <c>&lt;note&gt;</c> 內容（還沒換回原文）。</param>
    /// <param name="guardChanges">是否檢查改動幅度（只有自動整理檢查）。</param>
    public static NoteRejectReason Check(ContextNoteDocument maskedBefore, string scribeMarkdown, bool guardChanges)
    {
        ArgumentNullException.ThrowIfNull(maskedBefore);
        ArgumentNullException.ThrowIfNull(scribeMarkdown);

        if (!ContextNoteDocument.HasExactStructure(scribeMarkdown))
            return NoteRejectReason.WrongStructure;
        if (scribeMarkdown.Length > ContextNoteLimits.NoteMaxChars)
            return NoteRejectReason.TooLong;
        if (guardChanges && IsTooManyChanges(maskedBefore, scribeMarkdown))
            return NoteRejectReason.TooManyChanges;
        return NoteRejectReason.None;
    }

    /// <summary>舊研究本裡不是佔位符的區塊至少 <see cref="ContextNoteLimits.ChangeGuardMinBlocks"/> 個，而且超過一半在新研究本裡找不到一模一樣的。</summary>
    private static bool IsTooManyChanges(ContextNoteDocument maskedBefore, string scribeMarkdown)
    {
        var oldTexts = maskedBefore.AllBlocks()
            .Select(x => x.Block.Text)
            .Where(text => !WholePlaceholder.IsMatch(StripListMarker(text)))
            .ToArray();
        if (oldTexts.Length < ContextNoteLimits.ChangeGuardMinBlocks)
            return false;

        var newTexts = ContextNoteDocument.Parse(scribeMarkdown).AllBlocks().Select(x => x.Block.Text).ToHashSet(StringComparer.Ordinal);
        var missing = oldTexts.Count(text => !newTexts.Contains(text));
        return missing > oldTexts.Length * ContextNoteLimits.ChangeGuardRatio;
    }

    private static string StripListMarker(string text) => text.StartsWith("- ", StringComparison.Ordinal) ? text[2..] : text;
}
