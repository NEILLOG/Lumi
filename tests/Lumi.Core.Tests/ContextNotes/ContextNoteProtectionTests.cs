using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35B：<see cref="ContextNoteProtection"/>（佔位符的遮蔽、收回、使用者編輯後的保護清單、清理）。</summary>
public class ContextNoteProtectionTests
{
    private static ContextNoteDocument Breakdown(params string[] texts) =>
        ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Breakdown, texts.Select(t => new NoteBlock(t)).ToArray());

    private static ContextNoteDocument Doc(string section, params string[] texts) =>
        ContextNoteDocument.Empty.WithSection(section, texts.Select(t => new NoteBlock(t)).ToArray());

    private static IReadOnlyList<string> Texts(ContextNoteDocument doc, string section) =>
        doc.Section(section).Blocks.Select(b => b.Text).ToArray();

    [Fact]
    public void Mask_NumbersInDocumentOrder_ListAndParagraphPlaceholders()
    {
        var doc = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("- a"), new NoteBlock("段落"), new NoteBlock("- b")])
            .WithSection(ContextNoteDocument.Decided, [new NoteBlock("段落2")]);

        var result = ContextNoteProtection.Mask(doc, ["段落2", "- a", "- b"]);

        Assert.Equal(
            [new ProtectedEntry(1, ContextNoteDocument.Breakdown, "- a"),
             new ProtectedEntry(2, ContextNoteDocument.Breakdown, "- b"),
             new ProtectedEntry(3, ContextNoteDocument.Decided, "段落2")],
            result.Entries);
        Assert.Contains("- [[保護:1]]\n\n段落\n\n- [[保護:2]]\n\n## 已決定\n\n[[保護:3]]\n\n", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("- a", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_ProtectedTextNotInDocument_IsIgnored()
    {
        var result = ContextNoteProtection.Mask(Breakdown("- a"), ["不存在的", "- a"]);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(new ProtectedEntry(1, ContextNoteDocument.Breakdown, "- a"), entry);
    }

    [Fact]
    public void Mask_SameTextTwice_BothAreReplacedWithTheirOwnNumbers()
    {
        var doc = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("同一段")])
            .WithSection(ContextNoteDocument.Side, [new NoteBlock("同一段")]);

        var result = ContextNoteProtection.Mask(doc, ["同一段"]);

        Assert.Equal([1, 2], result.Entries.Select(e => e.Index));
        Assert.Equal([ContextNoteDocument.Breakdown, ContextNoteDocument.Side], result.Entries.Select(e => e.Section));
        Assert.Contains("[[保護:1]]", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("[[保護:2]]", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Unmask_RuleOne_WholePlaceholderBlockIsReplacedWithOriginal()
    {
        var scribe = Breakdown("- [[保護:1]]", "[[保護:2]]");
        var entries = new[] {
            new ProtectedEntry(1, ContextNoteDocument.Breakdown, "- a\n  子行"),
            new ProtectedEntry(2, ContextNoteDocument.Breakdown, "一段原文"),
        };

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(["- a\n  子行", "一段原文"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(0, result.RestoredCount);
    }

    [Fact]
    public void Unmask_RuleTwo_MarkerInsideSentenceIsRemoved_AndNumberCountsAsLost()
    {
        var scribe = Breakdown("前面 [[保護:1]]");
        var entries = new[] { new ProtectedEntry(1, ContextNoteDocument.Breakdown, "原文") };

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(["前面", "原文"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(1, result.RestoredCount);
    }

    [Fact]
    public void Unmask_RuleTwo_ListMarkerLeftAlone_IsDropped()
    {
        var scribe = Breakdown("-[[保護:1]]");
        var entries = new[] { new ProtectedEntry(1, ContextNoteDocument.Breakdown, "原文") };

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(["原文"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(1, result.RestoredCount);
    }

    [Fact]
    public void Unmask_RuleTwo_BlockEmptyAfterRemovingMarker_IsDropped()
    {
        var scribe = Breakdown("  [[保護:1]]", "保留");
        var entries = new[] { new ProtectedEntry(1, ContextNoteDocument.Breakdown, "原文") };

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(["保留", "原文"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(1, result.RestoredCount);
    }

    [Fact]
    public void Unmask_RuleThree_DuplicateWholeBlock_KeepsOnlyTheFirst()
    {
        var scribe = Breakdown("- [[保護:1]]", "中間", "- [[保護:1]]");
        var entries = new[] { new ProtectedEntry(1, ContextNoteDocument.Breakdown, "- a") };

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(["- a", "中間"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(0, result.RestoredCount);
    }

    [Fact]
    public void Unmask_RuleThree_UnknownWholeBlockIsDropped_UnknownMarkerInTextIsRemoved()
    {
        var scribe = Breakdown("- [[保護:9]]", "句子 [[保護:8]] 尾");
        var entries = new[] { new ProtectedEntry(1, ContextNoteDocument.Breakdown, "原文") };

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(["句子  尾", "原文"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(1, result.RestoredCount);
    }

    [Fact]
    public void Unmask_RuleFour_LostEntriesGoToTheEndOfTheirSection_InNumberOrder()
    {
        var scribe = Breakdown("段落");
        var entries = new[] {
            new ProtectedEntry(1, ContextNoteDocument.Breakdown, "A"),
            new ProtectedEntry(2, ContextNoteDocument.Decided, "- B"),
            new ProtectedEntry(3, ContextNoteDocument.Breakdown, "C"),
        };

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(["段落", "A", "C"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(["- B"], Texts(result.Document, ContextNoteDocument.Decided));
        Assert.Equal(3, result.RestoredCount);
    }

    [Fact]
    public void Unmask_EverythingComesBackAsItWas_RestoredCountIsZero()
    {
        var entries = new[] {
            new ProtectedEntry(1, ContextNoteDocument.Breakdown, "- a"),
            new ProtectedEntry(2, ContextNoteDocument.Decided, "段落"),
        };
        var scribe = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("- [[保護:1]]")])
            .WithSection(ContextNoteDocument.Decided, [new NoteBlock("[[保護:2]]")]);

        var result = ContextNoteProtection.Unmask(scribe, entries);

        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(["- a"], Texts(result.Document, ContextNoteDocument.Breakdown));
        Assert.Equal(["段落"], Texts(result.Document, ContextNoteDocument.Decided));
    }

    [Fact]
    public void Mask_then_Unmask_OfUnchangedDocument_GivesBackTheSameDocument()
    {
        var doc = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("- a"), new NoteBlock("段落")])
            .WithSection(ContextNoteDocument.Views, [new NoteBlock("- b\n  子")]);
        var masked = ContextNoteProtection.Mask(doc, ["- a", "- b\n  子"]);

        var result = ContextNoteProtection.Unmask(ContextNoteDocument.Parse(masked.Markdown), masked.Entries);

        Assert.Equal(doc, result.Document);
        Assert.Equal(0, result.RestoredCount);
    }

    [Fact]
    public void AfterUserEdit_UnchangedAiBlock_IsNotProtected_ChangedOrNewBlocksAre()
    {
        var previous = Breakdown("- AI1", "- AI2", "- 受保護");
        var edited = Breakdown("- AI1", "- AI2 改", "- 受保護", "- 新增");

        var result = ContextNoteProtection.AfterUserEdit(previous, ["- 受保護"], edited);

        Assert.Equal(["- AI2 改", "- 受保護", "- 新增"], result);
    }

    [Fact]
    public void AfterUserEdit_RemovedBlocks_AreGone()
    {
        var previous = Breakdown("- AI1", "- 受保護");
        var edited = Breakdown("- AI1", "- 新增");

        var result = ContextNoteProtection.AfterUserEdit(previous, ["- 受保護"], edited);

        Assert.Equal(["- 新增"], result);
    }

    [Fact]
    public void AfterUserEdit_IsDistinctAndFollowsEditedOrder()
    {
        var previous = Breakdown("- 舊");
        var edited = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Side, [new NoteBlock("- 新"), new NoteBlock("- 新")])
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("- 舊"), new NoteBlock("- 新2")]);

        var result = ContextNoteProtection.AfterUserEdit(previous, [], edited);

        // 依 edited 的文件順序（問題拆解在前），不是加入的順序。
        Assert.Equal(["- 新2", "- 新"], result);
    }

    [Fact]
    public void Prune_KeepsOnlyTextsStillInTheDocument_InOrder_WithoutDuplicates()
    {
        var doc = Breakdown("- a", "- c");

        var result = ContextNoteProtection.Prune(doc, ["- a", "- b", "- a", "- c"]);

        Assert.Equal(["- a", "- c"], result);
    }
}
