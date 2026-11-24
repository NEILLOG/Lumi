using Lumi.App.Markdown;

namespace Lumi.App.Tests.Markdown;

/// <summary>表格複製鈕放進剪貼簿的純文字（<see cref="MarkdownClipboard.ToTabText"/>）：Tab 分隔，貼到 Excel 會自動分欄。</summary>
public class MarkdownTableTextTests
{
    private static MdTable Table(string markdown) => MarkdownParser.Parse(markdown).OfType<MdTable>().Single();

    [Fact]
    public void EmptyCellsKeepTheirColumn()
    {
        var text = MarkdownClipboard.ToTabText(Table("| A | B | C |\n| --- | --- | --- |\n| 1 |  | 3 |"));

        Assert.Equal("A\tB\tC\n1\t\t3", text);
    }

    [Fact]
    public void ShortRowsArePaddedToTheHeaderWidth()
    {
        var text = MarkdownClipboard.ToTabText(Table("| A | B | C |\n| --- | --- | --- |\n| 1 |"));

        Assert.Equal("A\tB\tC\n1\t\t", text);
    }

    [Fact]
    public void EscapedPipeIsJustAPipe()
    {
        var text = MarkdownClipboard.ToTabText(Table("| 欄一 | 欄二 |\n| --- | --- |\n| a \\| b | c |"));

        Assert.Equal("欄一\t欄二\na | b\tc", text);
    }

    [Fact]
    public void FormattingIsDroppedAndOnlyTheTextRemains()
    {
        var text = MarkdownClipboard.ToTabText(Table("| 項目 | 說明 |\n| --- | --- |\n| **重點** | [連結](https://example.com) `code` |"));

        Assert.Equal("項目\t說明\n重點\t連結 code", text);
    }
}
