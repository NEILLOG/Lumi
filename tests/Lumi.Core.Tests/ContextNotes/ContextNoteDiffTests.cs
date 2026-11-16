using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35C：研究本版本差異（區塊 LCS、字的修改配對）。</summary>
public class ContextNoteDiffTests
{
    private static ContextNoteDocument Doc(params (string Section, string[] Blocks)[] sections)
    {
        var doc = ContextNoteDocument.Empty;
        foreach (var (name, blocks) in sections)
            doc = doc.WithSection(name, blocks.Select(b => new NoteBlock(b)).ToArray());
        return doc;
    }

    private static DiffSection Section(IReadOnlyList<DiffSection> sections, string name) => sections.Single(s => s.Name == name);

    [Fact]
    public void IdenticalDocuments_AllSame_NoChanges()
    {
        var doc = Doc((ContextNoteDocument.Decided, ["- A", "- B"]), (ContextNoteDocument.Open, ["- C"]));

        var sections = ContextNoteDiff.Compute(doc, doc);

        Assert.Equal(ContextNoteDocument.SectionNames, sections.Select(s => s.Name).ToArray());
        Assert.All(sections, s => Assert.False(s.HasChanges));
        Assert.Equal(
            [new DiffBlock(DiffKind.Same, "- A", "- A", null), new DiffBlock(DiffKind.Same, "- B", "- B", null)],
            Section(sections, ContextNoteDocument.Decided).Blocks);
    }

    [Fact]
    public void OnlyAdded_ProducesAddedBlock()
    {
        var old = Doc((ContextNoteDocument.Decided, ["- A"]));
        var updated = Doc((ContextNoteDocument.Decided, ["- A", "- B"]));

        var section = Section(ContextNoteDiff.Compute(old, updated), ContextNoteDocument.Decided);

        Assert.True(section.HasChanges);
        Assert.Equal([new DiffBlock(DiffKind.Same, "- A", "- A", null), new DiffBlock(DiffKind.Added, null, "- B", null)], section.Blocks);
    }

    [Fact]
    public void OnlyRemoved_ProducesRemovedBlock()
    {
        var old = Doc((ContextNoteDocument.Decided, ["- A", "- B"]));
        var updated = Doc((ContextNoteDocument.Decided, ["- A"]));

        var section = Section(ContextNoteDiff.Compute(old, updated), ContextNoteDocument.Decided);

        Assert.Equal([new DiffBlock(DiffKind.Same, "- A", "- A", null), new DiffBlock(DiffKind.Removed, "- B", null, null)], section.Blocks);
    }

    [Fact]
    public void SimilarBlock_IsChanged_WithInlineSegments()
    {
        // 「- 用 A 方案」與「- 用 B 方案」：8 個字裡有 7 個相同，≥ 一半
        var old = Doc((ContextNoteDocument.Decided, ["- 用 A 方案"]));
        var updated = Doc((ContextNoteDocument.Decided, ["- 用 B 方案"]));

        var block = Section(ContextNoteDiff.Compute(old, updated), ContextNoteDocument.Decided).Blocks.Single();

        Assert.Equal(DiffKind.Changed, block.Kind);
        Assert.Equal("- 用 A 方案", block.OldText);
        Assert.Equal("- 用 B 方案", block.NewText);
        Assert.Equal(
            [
                new DiffSegment(DiffKind.Same, "- 用 "),
                new DiffSegment(DiffKind.Removed, "A"),
                new DiffSegment(DiffKind.Added, "B"),
                new DiffSegment(DiffKind.Same, " 方案"),
            ],
            block.Inline);
    }

    [Fact]
    public void DissimilarBlocks_StayAsRemovedAndAdded()
    {
        // 「- 甲乙丙丁戊己」與「- 子丑寅卯辰巳午」：只有「- 」相同，2 ÷ 9 < 0.5
        var old = Doc((ContextNoteDocument.Decided, ["- 甲乙丙丁戊己"]));
        var updated = Doc((ContextNoteDocument.Decided, ["- 子丑寅卯辰巳午"]));

        var blocks = Section(ContextNoteDiff.Compute(old, updated), ContextNoteDocument.Decided).Blocks;

        Assert.Equal([new DiffBlock(DiffKind.Removed, "- 甲乙丙丁戊己", null, null), new DiffBlock(DiffKind.Added, null, "- 子丑寅卯辰巳午", null)], blocks);
    }

    [Fact]
    public void BlockMovedToAnotherSection_IsRemovedInOneAndAddedInTheOther()
    {
        var old = Doc((ContextNoteDocument.Decided, ["- X"]));
        var updated = Doc((ContextNoteDocument.Open, ["- X"]));

        var sections = ContextNoteDiff.Compute(old, updated);

        Assert.Equal([new DiffBlock(DiffKind.Removed, "- X", null, null)], Section(sections, ContextNoteDocument.Decided).Blocks);
        Assert.Equal([new DiffBlock(DiffKind.Added, null, "- X", null)], Section(sections, ContextNoteDocument.Open).Blocks);
    }

    [Fact]
    public void BlockOfExactlyTheLimit_IsPaired()
    {
        var limit = ContextNoteLimits.InlineDiffMaxChars;
        var old = Doc((ContextNoteDocument.Decided, [new string('a', limit - 1) + "1"]));
        var updated = Doc((ContextNoteDocument.Decided, [new string('a', limit - 1) + "2"]));

        var block = Section(ContextNoteDiff.Compute(old, updated), ContextNoteDocument.Decided).Blocks.Single();

        Assert.Equal(DiffKind.Changed, block.Kind);
    }

    [Fact]
    public void BlockOverTheLimit_IsNotPaired()
    {
        var limit = ContextNoteLimits.InlineDiffMaxChars;
        var old = Doc((ContextNoteDocument.Decided, [new string('a', limit) + "1"]));
        var updated = Doc((ContextNoteDocument.Decided, [new string('a', limit) + "2"]));

        var blocks = Section(ContextNoteDiff.Compute(old, updated), ContextNoteDocument.Decided).Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.Equal(DiffKind.Removed, blocks[0].Kind);
        Assert.Equal(DiffKind.Added, blocks[1].Kind);
    }

    [Fact]
    public void ChangedBlocks_ReturnsNewBlocksWithNoIdenticalTextInOld()
    {
        var old = Doc((ContextNoteDocument.Decided, ["- A", "- B"]), (ContextNoteDocument.Open, ["- C"]));
        var updated = Doc((ContextNoteDocument.Decided, ["- A", "- B2"]), (ContextNoteDocument.Open, ["- C"]), (ContextNoteDocument.Breakdown, ["- 新"]));

        var changed = ContextNoteDiff.ChangedBlocks(old, updated);

        Assert.Equal(2, changed.Count);
        Assert.True(changed.SetEquals(new[] { "- B2", "- 新" }));
    }

    [Fact]
    public void ChangedBlocks_IdenticalDocuments_IsEmpty()
    {
        var doc = Doc((ContextNoteDocument.Decided, ["- A"]));

        Assert.Empty(ContextNoteDiff.ChangedBlocks(doc, doc));
    }
}
