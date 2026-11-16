namespace Lumi.Core.ContextNotes;

public enum NoteRejectReason { None, WrongStructure, TooLong, TooManyChanges }

/// <summary>書記輸出的程式端檢查（計畫 4.6）。</summary>
public static class ContextNoteValidator
{
    // TODO(35B)
    /// <param name="maskedBefore">送給書記的佔位符版研究本。</param>
    /// <param name="scribeMarkdown">書記回來的 <c>&lt;note&gt;</c> 內容（還沒換回原文）。</param>
    /// <param name="guardChanges">是否檢查改動幅度（只有自動整理檢查）。</param>
    public static NoteRejectReason Check(ContextNoteDocument maskedBefore, string scribeMarkdown, bool guardChanges) => throw new NotImplementedException();
}
