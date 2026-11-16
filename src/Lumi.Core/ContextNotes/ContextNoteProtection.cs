namespace Lumi.Core.ContextNotes;

/// <param name="Index">佔位符的編號（從 1 起）。</param>
/// <param name="Section">原本所在的區段。</param>
/// <param name="Text">原文。</param>
public sealed record ProtectedEntry(int Index, string Section, string Text);

/// <param name="Markdown">佔位符版的研究本（送給書記）。</param>
public sealed record MaskResult(string Markdown, IReadOnlyList<ProtectedEntry> Entries);

/// <param name="RestoredCount">被移動或刪除、程式放回去的受保護區塊數。</param>
public sealed record UnmaskResult(ContextNoteDocument Document, int RestoredCount);

/// <summary>受保護的內容（計畫 4.5，D10）：送給書記前換成佔位符，收回後換回原文。</summary>
public static class ContextNoteProtection
{
    public static string Placeholder(int index) => $"[[保護:{index}]]";

    // TODO(35B)
    public static MaskResult Mask(ContextNoteDocument document, IReadOnlyCollection<string> protectedBlocks) => throw new NotImplementedException();

    // TODO(35B)
    public static UnmaskResult Unmask(ContextNoteDocument scribeOutput, IReadOnlyList<ProtectedEntry> entries) => throw new NotImplementedException();

    // TODO(35B)
    /// <summary>使用者存檔後的保護清單（4.5）。</summary>
    public static IReadOnlyList<string> AfterUserEdit(ContextNoteDocument previous, IReadOnlyCollection<string> previousProtected, ContextNoteDocument edited) =>
        throw new NotImplementedException();

    // TODO(35B)
    /// <summary>拿掉文件裡已經不存在的項目（存檔前呼叫）。</summary>
    public static IReadOnlyList<string> Prune(ContextNoteDocument document, IReadOnlyCollection<string> protectedBlocks) => throw new NotImplementedException();
}
