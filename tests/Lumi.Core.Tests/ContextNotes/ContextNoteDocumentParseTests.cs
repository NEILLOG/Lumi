using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35B：<see cref="ContextNoteDocument.Parse"/>、<see cref="ContextNoteDocument.HasExactStructure"/>、<see cref="ContextNoteDocument.ToMarkdown"/> 的切法與輸出。</summary>
public class ContextNoteDocumentParseTests
{
    private static readonly string[] Names = ContextNoteDocument.SectionNames.ToArray();

    /// <summary>照名稱順序產生的標題（每個標題後空一行），給 <see cref="ContextNoteDocument.HasExactStructure"/> 用。</summary>
    private static string Headings(params string[] names) => string.Concat(names.Select(n => $"## {n}\n\n"));

    private static IReadOnlyList<string> Texts(ContextNoteDocument doc, string section) =>
        doc.Section(section).Blocks.Select(b => b.Text).ToArray();

    [Fact]
    public void Parse_NullOrBlank_ReturnsEmpty()
    {
        Assert.Equal(ContextNoteDocument.Empty, ContextNoteDocument.Parse(null!));
        Assert.Equal(ContextNoteDocument.Empty, ContextNoteDocument.Parse(""));
        Assert.Equal(ContextNoteDocument.Empty, ContextNoteDocument.Parse("  \n\t\n"));
    }

    [Fact]
    public void EmptyDocument_ToMarkdown_IsSevenHeadingsSeparatedByBlankLines()
    {
        Assert.Equal(
            "## 問題拆解\n\n## 已決定\n\n## 待決問題\n\n## 觀點與選項\n\n## 否決的選項\n\n## 目前的解與待驗證\n\n## 旁支\n",
            ContextNoteDocument.Empty.ToMarkdown());
    }

    [Fact]
    public void RoundTrip_AllSevenSections_ParseOfToMarkdownEqualsOriginal()
    {
        var doc = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("- 拆解一"), new NoteBlock("拆解段落")])
            .WithSection(ContextNoteDocument.Decided, [new NoteBlock("- 已決定 A（使用者原話：「好」）")])
            .WithSection(ContextNoteDocument.Open, [new NoteBlock("待決的段落\n第二行")])
            .WithSection(ContextNoteDocument.Views, [new NoteBlock("- 觀點 1\n  子行\n\n  子行後空行"), new NoteBlock("- 觀點 2")])
            .WithSection(ContextNoteDocument.Rejected, [new NoteBlock("- 否決")])
            .WithSection(ContextNoteDocument.Solution, [new NoteBlock("### 小標\n| 欄 | 欄 |\n|---|---|\n| a | b |")])
            .WithSection(ContextNoteDocument.Side, [new NoteBlock("1. 第一\n   說明"), new NoteBlock("2. 第二")]);

        var parsed = ContextNoteDocument.Parse(doc.ToMarkdown());

        Assert.Equal(doc, parsed);
        Assert.Equal(doc.ToMarkdown(), parsed.ToMarkdown());
    }

    [Fact]
    public void Parse_ListBlock_KeepsIndentedSubLinesAndBlankLinesBetweenThem()
    {
        var doc = ContextNoteDocument.Parse("""
            ## 問題拆解
            - 第一項
              子行一

              子行二
            - 第二項
            """);

        Assert.Equal(["- 第一項\n  子行一\n\n  子行二", "- 第二項"], Texts(doc, ContextNoteDocument.Breakdown));
    }

    [Fact]
    public void Parse_ListBlock_BlankLineBeforeNonIndentedLineIsNotPartOfTheBlock()
    {
        var doc = ContextNoteDocument.Parse("""
            ## 已決定
            - 項目

            後面的段落
            """);

        Assert.Equal(["- 項目", "後面的段落"], Texts(doc, ContextNoteDocument.Decided));
    }

    [Fact]
    public void Parse_ParagraphBlocks_SubHeadingTableAndImageLine()
    {
        var doc = ContextNoteDocument.Parse("""
            ## 觀點與選項
            ### 小標
            段落一
            段落一續行

            | 欄 | 欄 |
            |---|---|
            | a | b |

            ![圖](images/a.png)
            """);

        Assert.Equal(
            ["### 小標\n段落一\n段落一續行", "| 欄 | 欄 |\n|---|---|\n| a | b |", "![圖](images/a.png)"],
            Texts(doc, ContextNoteDocument.Views));
    }

    [Fact]
    public void Parse_ListItemFollowedByParagraphWithoutBlankLine_StartsANewParagraphBlock()
    {
        var doc = ContextNoteDocument.Parse("""
            ## 旁支
            - 清單
            緊接的一般行
            - 另一個清單
            """);

        Assert.Equal(["- 清單", "緊接的一般行", "- 另一個清單"], Texts(doc, ContextNoteDocument.Side));
    }

    [Fact]
    public void Parse_CodeFence_DoesNotSplitOnListMarkersHeadingsOrBlankLines()
    {
        var doc = ContextNoteDocument.Parse("""
            ## 問題拆解
            ```text
            - 不切
            ## 也不切

            空行也不切
            ```
            - 清單後
            """);

        Assert.Equal(
            ["```text\n- 不切\n## 也不切\n\n空行也不切\n```", "- 清單後"],
            Texts(doc, ContextNoteDocument.Breakdown));
        Assert.Empty(Texts(doc, ContextNoteDocument.Decided));
    }

    [Fact]
    public void Parse_MissingSections_AreFilledWithEmptySections()
    {
        var doc = ContextNoteDocument.Parse("## 已決定\n\n- a\n");

        Assert.Equal(Names, doc.Sections.Select(s => s.Name));
        Assert.Equal(["- a"], Texts(doc, ContextNoteDocument.Decided));
        Assert.Single(doc.AllBlocks());
    }

    [Fact]
    public void Parse_UnknownLevel2Heading_BecomesLevel3LineInThePreviousSection()
    {
        var doc = ContextNoteDocument.Parse("## 已決定\n- a\n## 其他\n- b\n");

        Assert.Equal(["- a", "### 其他", "- b"], Texts(doc, ContextNoteDocument.Decided));
        Assert.Empty(Texts(doc, ContextNoteDocument.Breakdown));
    }

    [Fact]
    public void Parse_TextBeforeTheFirstHeading_MovesToTheFrontOfBreakdown()
    {
        var doc = ContextNoteDocument.Parse("前言一行\n\n## 問題拆解\n- 拆\n\n## 已決定\n- 決");

        Assert.Equal(["前言一行", "- 拆"], Texts(doc, ContextNoteDocument.Breakdown));
        Assert.Equal(["- 決"], Texts(doc, ContextNoteDocument.Decided));
    }

    [Fact]
    public void Parse_TextBeforeTheFirstHeadingWithoutBreakdownHeading_StillGoesToBreakdown()
    {
        var doc = ContextNoteDocument.Parse("前言\n## 已決定\n- 決");

        Assert.Equal(["前言"], Texts(doc, ContextNoteDocument.Breakdown));
        Assert.Equal(["- 決"], Texts(doc, ContextNoteDocument.Decided));
    }

    [Fact]
    public void Parse_RepeatedHeading_AppendsSecondOccurrenceAfterTheFirst()
    {
        var doc = ContextNoteDocument.Parse("## 已決定\n- a\n\n## 旁支\n- s\n\n## 已決定\n- b");

        Assert.Equal(["- a", "- b"], Texts(doc, ContextNoteDocument.Decided));
        Assert.Equal(["- s"], Texts(doc, ContextNoteDocument.Side));
    }

    [Fact]
    public void Parse_CrlfAndCr_AreNormalized()
    {
        var crlf = ContextNoteDocument.Parse("## 已決定\r\n- a\r\n- b\r\n");
        var cr = ContextNoteDocument.Parse("## 已決定\r- a\r- b\r");

        Assert.Equal(["- a", "- b"], Texts(crlf, ContextNoteDocument.Decided));
        Assert.Equal(crlf, cr);
    }

    [Fact]
    public void ToMarkdown_AdjacentListBlocks_SeparatedByOneNewline_OtherwiseBlankLine()
    {
        var doc = ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Breakdown, [
            new NoteBlock("- a"),
            new NoteBlock("- b"),
            new NoteBlock("段落"),
            new NoteBlock("段落二"),
            new NoteBlock("1. 數字"),
        ]);

        Assert.Equal(
            "## 問題拆解\n\n- a\n- b\n\n段落\n\n段落二\n\n1. 數字\n\n## 已決定\n\n## 待決問題\n\n## 觀點與選項\n\n## 否決的選項\n\n## 目前的解與待驗證\n\n## 旁支\n",
            doc.ToMarkdown());
    }

    [Fact]
    public void ToMarkdown_ParagraphThenList_IsSeparatedByBlankLine()
    {
        var doc = ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Side, [new NoteBlock("段落"), new NoteBlock("- 清單")]);

        Assert.Contains("## 旁支\n\n段落\n\n- 清單\n", doc.ToMarkdown());
        Assert.EndsWith("\n", doc.ToMarkdown());
        Assert.False(doc.ToMarkdown().EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void HasExactStructure_ValidStructure_Passes()
    {
        Assert.True(ContextNoteDocument.HasExactStructure(ContextNoteDocument.Empty.ToMarkdown()));
    }

    [Fact]
    public void HasExactStructure_MissingOneHeading_Fails()
    {
        var md = Headings(Names.Where(n => n != ContextNoteDocument.Side).ToArray());

        Assert.False(ContextNoteDocument.HasExactStructure(md));
    }

    [Fact]
    public void HasExactStructure_WrongOrder_Fails()
    {
        var swapped = Names.ToArray();
        (swapped[1], swapped[2]) = (swapped[2], swapped[1]);

        Assert.False(ContextNoteDocument.HasExactStructure(Headings(swapped)));
    }

    [Fact]
    public void HasExactStructure_ExtraLevel2Heading_Fails()
    {
        var md = Headings(Names) + "## 其他\n\n";

        Assert.False(ContextNoteDocument.HasExactStructure(md));
    }

    [Fact]
    public void HasExactStructure_TextBeforeTheFirstHeading_Fails()
    {
        Assert.False(ContextNoteDocument.HasExactStructure("前言\n" + Headings(Names)));
    }

    [Fact]
    public void HasExactStructure_HeadingWithTrailingSpace_Fails()
    {
        var md = Headings(Names).Replace("## 已決定\n", "## 已決定 \n", StringComparison.Ordinal);

        Assert.False(ContextNoteDocument.HasExactStructure(md));
    }

    [Fact]
    public void HasExactStructure_NoHeadings_Fails()
    {
        Assert.False(ContextNoteDocument.HasExactStructure("只是一段文字\n- 清單"));
        Assert.False(ContextNoteDocument.HasExactStructure(""));
    }

    [Fact]
    public void HasExactStructure_LevelTwoHeadingInsideCodeFence_IsIgnored()
    {
        var md = "## 問題拆解\n\n```\n## 其他\n```\n\n" + Headings(Names.Skip(1).ToArray());

        Assert.True(ContextNoteDocument.HasExactStructure(md));
    }
}
