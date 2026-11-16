using System.Text;

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
    private enum Step { Same, Removed, Added }

    /// <param name="I">舊序列的索引（Same、Removed 有效）。</param>
    /// <param name="J">新序列的索引（Same、Added 有效）。</param>
    private readonly record struct Op(Step Kind, int I, int J);

    /// <summary>每個區段各自比對區塊（文字完全相同＝同一個），回傳 7 個區段，順序照 <see cref="ContextNoteDocument.SectionNames"/>。</summary>
    public static IReadOnlyList<DiffSection> Compute(ContextNoteDocument oldDocument, ContextNoteDocument newDocument)
    {
        ArgumentNullException.ThrowIfNull(oldDocument);
        ArgumentNullException.ThrowIfNull(newDocument);
        return ContextNoteDocument.SectionNames
            .Select(name => DiffOneSection(name,
                oldDocument.Section(name).Blocks.Select(b => b.Text).ToArray(),
                newDocument.Section(name).Blocks.Select(b => b.Text).ToArray()))
            .ToArray();
    }

    /// <summary>新文件裡、舊文件沒有一模一樣文字的區塊（「剛更新」用）。</summary>
    public static IReadOnlySet<string> ChangedBlocks(ContextNoteDocument oldDocument, ContextNoteDocument newDocument)
    {
        ArgumentNullException.ThrowIfNull(oldDocument);
        ArgumentNullException.ThrowIfNull(newDocument);
        var oldTexts = new HashSet<string>(oldDocument.AllBlocks().Select(b => b.Block.Text), StringComparer.Ordinal);
        return newDocument.AllBlocks()
            .Select(b => b.Block.Text)
            .Where(text => !oldTexts.Contains(text))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static DiffSection DiffOneSection(string name, string[] oldBlocks, string[] newBlocks)
    {
        var ops = Align<string>(oldBlocks, newBlocks, StringComparer.Ordinal);
        var result = new List<DiffBlock>();
        var index = 0;
        while (index < ops.Count)
        {
            if (ops[index].Kind == Step.Same)
            {
                var text = oldBlocks[ops[index].I];
                result.Add(new DiffBlock(DiffKind.Same, text, text, null));
                index++;
                continue;
            }

            // 兩個相同區塊之間的一段：刪除的依序和新增的依序兩兩配對
            var removed = new List<string>();
            var added = new List<string>();
            while (index < ops.Count && ops[index].Kind != Step.Same)
            {
                if (ops[index].Kind == Step.Removed)
                    removed.Add(oldBlocks[ops[index].I]);
                else
                    added.Add(newBlocks[ops[index].J]);
                index++;
            }
            for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                if (k < removed.Count && k < added.Count)
                {
                    var (oldText, newText) = (removed[k], added[k]);
                    var inline = TryInline(oldText, newText);
                    if (inline is not null)
                    {
                        result.Add(new DiffBlock(DiffKind.Changed, oldText, newText, inline));
                        continue;
                    }
                    result.Add(new DiffBlock(DiffKind.Removed, oldText, null, null));
                    result.Add(new DiffBlock(DiffKind.Added, null, newText, null));
                }
                else if (k < removed.Count)
                    result.Add(new DiffBlock(DiffKind.Removed, removed[k], null, null));
                else
                    result.Add(new DiffBlock(DiffKind.Added, null, added[k], null));
            }
        }
        return new DiffSection(name, result);
    }

    /// <summary>
    /// 兩個配對的區塊做字的比對。相同字數 ÷ 較長那一邊 ≥ 0.5 才回傳 Inline；任一邊超過 <see cref="ContextNoteLimits.InlineDiffMaxChars"/> 或不夠像就回 null。
    /// </summary>
    private static IReadOnlyList<DiffSegment>? TryInline(string oldText, string newText)
    {
        if (oldText.Length > ContextNoteLimits.InlineDiffMaxChars || newText.Length > ContextNoteLimits.InlineDiffMaxChars)
            return null;
        var ops = Align<char>(oldText.ToCharArray(), newText.ToCharArray(), EqualityComparer<char>.Default);
        var same = ops.Count(o => o.Kind == Step.Same);
        if (same * 2 < Math.Max(oldText.Length, newText.Length))
            return null;

        var segments = new List<DiffSegment>();
        var kind = DiffKind.Same;
        var buffer = new StringBuilder();
        foreach (var op in ops)
        {
            var (opKind, ch) = op.Kind switch
            {
                Step.Same => (DiffKind.Same, oldText[op.I]),
                Step.Removed => (DiffKind.Removed, oldText[op.I]),
                _ => (DiffKind.Added, newText[op.J]),
            };
            if (buffer.Length > 0 && opKind != kind)
            {
                segments.Add(new DiffSegment(kind, buffer.ToString()));
                buffer.Clear();
            }
            kind = opKind;
            buffer.Append(ch);
        }
        if (buffer.Length > 0)
            segments.Add(new DiffSegment(kind, buffer.ToString()));
        return segments;
    }

    /// <summary>
    /// LCS 對齊（後綴表）。相等時一定配對（這是後綴 LCS 的最佳選擇）；否則先刪舊的或先加新的，依 LCS 長度決定。
    /// </summary>
    private static List<Op> Align<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T> comparer)
    {
        var n = a.Count;
        var m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = comparer.Equals(a[i], b[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var ops = new List<Op>();
        var x = 0;
        var y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && comparer.Equals(a[x], b[y]))
            {
                ops.Add(new Op(Step.Same, x, y));
                x++;
                y++;
            }
            else if (y >= m || (x < n && dp[x + 1, y] >= dp[x, y + 1]))
            {
                ops.Add(new Op(Step.Removed, x, y));
                x++;
            }
            else
            {
                ops.Add(new Op(Step.Added, x, y));
                y++;
            }
        }
        return ops;
    }
}
