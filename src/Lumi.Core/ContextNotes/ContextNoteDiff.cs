namespace Lumi.Core.ContextNotes;

public enum DiffKind { Same, Added, Removed, Changed }

/// <summary>字的比對結果的一段；只會是 Same／Added／Removed。</summary>
public sealed record DiffSegment(DiffKind Kind, string Text);

/// <param name="Inline">只有 <see cref="DiffKind.Changed"/> 有：字的比對。</param>
public sealed record DiffBlock(DiffKind Kind, string? OldText, string? NewText, IReadOnlyList<DiffSegment>? Inline);

public sealed record DiffSection(string Name, IReadOnlyList<DiffBlock> Blocks)
{
    public bool HasChanges => Blocks.Any(b => b.Kind != DiffKind.Same);
}

/// <summary>兩版研究本的差異（計畫 4.11「版本歷史」、E7：自己用 LCS 算，不加套件）。</summary>
public static class ContextNoteDiff
{
    // TODO(35C)
    public static IReadOnlyList<DiffSection> Compute(ContextNoteDocument oldDocument, ContextNoteDocument newDocument) => throw new NotImplementedException();

    // TODO(35C)
    /// <summary>新文件裡、舊文件沒有一模一樣文字的區塊（「剛更新」用）。</summary>
    public static IReadOnlySet<string> ChangedBlocks(ContextNoteDocument oldDocument, ContextNoteDocument newDocument) => throw new NotImplementedException();
}
