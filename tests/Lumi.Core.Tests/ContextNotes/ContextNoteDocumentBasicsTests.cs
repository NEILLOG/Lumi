using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 Phase 0：<see cref="ContextNoteDocument"/> 不含解析的成員（建構子、Empty、Section、AllBlocks、WithSection、相等）與 <see cref="NoteBlock.IsListItem"/>。</summary>
public class ContextNoteDocumentBasicsTests
{
    [Fact]
    public void Empty_HasTheSevenSectionsInOrder_AllEmpty()
    {
        var doc = ContextNoteDocument.Empty;

        Assert.Equal(ContextNoteDocument.SectionNames, doc.Sections.Select(s => s.Name));
        Assert.Equal(["問題拆解", "已決定", "待決問題", "觀點與選項", "否決的選項", "目前的解與待驗證", "旁支"], ContextNoteDocument.SectionNames);
        Assert.All(doc.Sections, s => Assert.Empty(s.Blocks));
        Assert.Empty(doc.AllBlocks());
    }

    [Fact]
    public void Constructor_FillsMissingSections_AndOrdersThem()
    {
        var doc = new ContextNoteDocument([
            new NoteSection(ContextNoteDocument.Side, [new NoteBlock("- 旁")]),
            new NoteSection(ContextNoteDocument.Breakdown, [new NoteBlock("- 拆")]),
        ]);

        Assert.Equal(ContextNoteDocument.SectionNames, doc.Sections.Select(s => s.Name));
        Assert.Equal("- 拆", Assert.Single(doc.Section(ContextNoteDocument.Breakdown).Blocks).Text);
        Assert.Equal("- 旁", Assert.Single(doc.Section(ContextNoteDocument.Side).Blocks).Text);
        Assert.Empty(doc.Section(ContextNoteDocument.Decided).Blocks);
    }

    [Fact]
    public void Constructor_RejectsUnknownOrDuplicateSections()
    {
        Assert.Throws<ArgumentException>(() => new ContextNoteDocument([new NoteSection("其他", [])]));
        Assert.Throws<ArgumentException>(() => new ContextNoteDocument([
            new NoteSection(ContextNoteDocument.Decided, []), new NoteSection(ContextNoteDocument.Decided, []),
        ]));
    }

    [Fact]
    public void Section_UnknownName_Throws()
    {
        Assert.Throws<ArgumentException>(() => ContextNoteDocument.Empty.Section("不存在"));
    }

    [Fact]
    public void WithSection_ReturnsANewDocument_AndLeavesTheOriginalAlone()
    {
        var original = ContextNoteDocument.Empty;
        var changed = original.WithSection(ContextNoteDocument.Decided, [new NoteBlock("- 用 A")]);

        Assert.Empty(original.Section(ContextNoteDocument.Decided).Blocks);
        Assert.Equal("- 用 A", Assert.Single(changed.Section(ContextNoteDocument.Decided).Blocks).Text);
        Assert.Throws<ArgumentException>(() => original.WithSection("不存在", []));
    }

    [Fact]
    public void AllBlocks_FollowsDocumentOrder()
    {
        var doc = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Side, [new NoteBlock("- 3")])
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("- 1"), new NoteBlock("段落 2")]);

        Assert.Equal(
            [(ContextNoteDocument.Breakdown, "- 1"), (ContextNoteDocument.Breakdown, "段落 2"), (ContextNoteDocument.Side, "- 3")],
            doc.AllBlocks().Select(x => (x.Section, x.Block.Text)));
    }

    [Fact]
    public void Documents_WithTheSameBlockTexts_AreEqual()
    {
        var a = ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Open, [new NoteBlock("- 問題")]);
        var b = ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Open, [new NoteBlock("- 問題")]);
        var c = ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Views, [new NoteBlock("- 問題")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, ContextNoteDocument.Empty);
    }

    [Theory]
    [InlineData("- 項目", true)]
    [InlineData("* 項目", true)]
    [InlineData("12. 項目", true)]
    [InlineData("-沒有空白", false)]
    [InlineData("  - 縮排", false)]
    [InlineData("### 小標", false)]
    [InlineData("一般段落", false)]
    public void IsListItem_LooksAtTheFirstLine(string text, bool expected)
    {
        Assert.Equal(expected, new NoteBlock(text + "\n  子行").IsListItem);
    }
}
