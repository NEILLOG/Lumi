using Lumi.App.Markdown;

namespace Lumi.App.Tests.Markdown;

public class MarkdownParserTests
{
    private static string Flat(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdCode c => c.Text,
        MdStrong s => Flat(s.Children),
        MdEmphasis e => Flat(e.Children),
        MdStrike s => Flat(s.Children),
        MdLink l => Flat(l.Children),
        MdLineBreak => "\n",
        _ => "?",
    }));

    [Fact]
    public void Empty_ReturnsNoBlocks()
    {
        Assert.Empty(MarkdownParser.Parse(""));
        Assert.Empty(MarkdownParser.Parse(null!));
    }

    [Fact]
    public void Heading_HasLevelAndText()
    {
        var blocks = MarkdownParser.Parse("# 一\n\n### 三");

        var h1 = Assert.IsType<MdHeading>(blocks[0]);
        var h3 = Assert.IsType<MdHeading>(blocks[1]);
        Assert.Equal((1, "一"), (h1.Level, Flat(h1.Inlines)));
        Assert.Equal((3, "三"), (h3.Level, Flat(h3.Inlines)));
    }

    [Fact]
    public void Paragraph_WithInlineStyles()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("普通 **粗** *斜* ~~刪~~ `code` 結尾")));

        Assert.Contains(p.Inlines, i => i is MdStrong s && Flat(s.Children) == "粗");
        Assert.Contains(p.Inlines, i => i is MdEmphasis e && Flat(e.Children) == "斜");
        Assert.Contains(p.Inlines, i => i is MdStrike s && Flat(s.Children) == "刪");
        Assert.Contains(p.Inlines, i => i is MdCode { Text: "code" });
        Assert.Equal("普通 粗 斜 刪 code 結尾", Flat(p.Inlines));
    }

    [Fact]
    public void BoldItalicCombined_NestsBothStyles()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("***全部***")));

        // ***x*** 會變成一層粗體包一層斜體（或反過來）：兩種樣式都要在，且文字只出現一次
        var outer = Assert.Single(p.Inlines);
        var innerChildren = outer switch
        {
            MdStrong s => s.Children,
            MdEmphasis e => e.Children,
            _ => throw new Xunit.Sdk.XunitException($"預期粗體或斜體，實際是 {outer.GetType().Name}"),
        };
        var inner = Assert.Single(innerChildren);
        Assert.True((outer is MdStrong && inner is MdEmphasis) || (outer is MdEmphasis && inner is MdStrong));
        Assert.Equal("全部", Flat(p.Inlines));
    }

    [Fact]
    public void SoftLineBreak_BecomesLineBreak()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("第一行\n第二行")));

        Assert.Equal("第一行\n第二行", Flat(p.Inlines));
        Assert.Contains(p.Inlines, i => i is MdLineBreak);
    }

    [Fact]
    public void FencedCode_KeepsLanguageAndCode()
    {
        var code = Assert.IsType<MdCodeBlock>(Assert.Single(MarkdownParser.Parse("```csharp\nvar x = 1;\nConsole.WriteLine(x);\n```")));

        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;\nConsole.WriteLine(x);", code.Code);
    }

    [Fact]
    public void FencedCode_WithoutLanguage_HasNullLanguage_AndEmptyBlockIsEmptyCode()
    {
        var blocks = MarkdownParser.Parse("```\nplain\n```\n\n```py\n```");

        Assert.Null(Assert.IsType<MdCodeBlock>(blocks[0]).Language);
        var empty = Assert.IsType<MdCodeBlock>(blocks[1]);
        Assert.Equal("py", empty.Language);
        Assert.Equal("", empty.Code);
    }

    [Fact]
    public void FencedCode_LanguageWithExtraInfo_TakesFirstWord()
    {
        var code = Assert.IsType<MdCodeBlock>(Assert.Single(MarkdownParser.Parse("```json title=\"x\"\n{}\n```")));

        Assert.Equal("json", code.Language);
    }

    [Fact]
    public void UnclosedFence_WhileStreaming_IsStillACodeBlockToTheEnd()
    {
        var blocks = MarkdownParser.Parse("前言\n\n```python\nprint(1)\nprint(2");

        Assert.IsType<MdParagraph>(blocks[0]);
        var code = Assert.IsType<MdCodeBlock>(blocks[1]);
        Assert.Equal("python", code.Language);
        Assert.Equal("print(1)\nprint(2", code.Code);
    }

    [Fact]
    public void UnclosedEmphasis_StaysLiteralUntilClosed()
    {
        var open = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("這是 **還沒關")));
        Assert.DoesNotContain(open.Inlines, i => i is MdStrong);
        Assert.Equal("這是 **還沒關", Flat(open.Inlines));

        var closed = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("這是 **關了**")));
        Assert.Contains(closed.Inlines, i => i is MdStrong);
    }

    [Fact]
    public void IndentedCode_IsACodeBlock()
    {
        var blocks = MarkdownParser.Parse("段落\n\n    縮排的程式碼");

        var code = Assert.IsType<MdCodeBlock>(blocks[1]);
        Assert.Null(code.Language);
        Assert.Equal("縮排的程式碼", code.Code);
    }

    [Fact]
    public void UnorderedList_Items()
    {
        var list = Assert.IsType<MdList>(Assert.Single(MarkdownParser.Parse("- 一\n- 二\n- 三")));

        Assert.False(list.Ordered);
        Assert.Equal(["一", "二", "三"], list.Items.Select(item => Flat(Assert.IsType<MdParagraph>(item[0]).Inlines)));
    }

    [Fact]
    public void OrderedList_KeepsStartNumber()
    {
        var list = Assert.IsType<MdList>(Assert.Single(MarkdownParser.Parse("3. 三\n4. 四")));

        Assert.True(list.Ordered);
        Assert.Equal(3, list.Start);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void NestedList_AndBlocksInsideItems()
    {
        var list = Assert.IsType<MdList>(Assert.Single(MarkdownParser.Parse("- 外層\n  - 內層一\n  - 內層二\n- 外層二\n\n  ```\n  code\n  ```")));

        var firstItem = list.Items[0];
        Assert.IsType<MdParagraph>(firstItem[0]);
        var nested = Assert.IsType<MdList>(firstItem[1]);
        Assert.Equal(2, nested.Items.Count);
        Assert.Contains(list.Items[1], b => b is MdCodeBlock);
    }

    [Fact]
    public void Quote_ContainsBlocks()
    {
        var quote = Assert.IsType<MdQuote>(Assert.Single(MarkdownParser.Parse("> 引用一\n>\n> - 引用清單")));

        Assert.IsType<MdParagraph>(quote.Blocks[0]);
        Assert.IsType<MdList>(quote.Blocks[1]);
    }

    [Fact]
    public void ThematicBreak_IsRule()
    {
        var blocks = MarkdownParser.Parse("上\n\n---\n\n下");

        Assert.IsType<MdRule>(blocks[1]);
    }

    [Fact]
    public void Table_HeaderRowsAndAlignments()
    {
        var table = Assert.IsType<MdTable>(Assert.Single(MarkdownParser.Parse("| 名稱 | 數量 | 備註 |\n|:---|---:|:---:|\n| 蘋果 | 3 | **好** |\n| 梨子 | 12 | |")));

        Assert.Equal(["名稱", "數量", "備註"], table.Header.Select(c => Flat(c.Inlines)));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["蘋果", "3", "好"], table.Rows[0].Select(c => Flat(c.Inlines)));
        Assert.Equal([MdAlign.Left, MdAlign.Right, MdAlign.Center], table.Alignments);
        Assert.Contains(table.Rows[0][2].Inlines, i => i is MdStrong);
    }

    [Fact]
    public void Link_HasUrlAndLabel()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("看 [官網](https://example.com/a?b=1) 吧")));

        var link = Assert.IsType<MdLink>(Assert.Single(p.Inlines, i => i is MdLink));
        Assert.Equal("https://example.com/a?b=1", link.Url);
        Assert.Equal("官網", Flat(link.Children));
    }

    [Fact]
    public void BareUrl_BecomesLink()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("請見 https://example.com 的說明")));

        var link = Assert.IsType<MdLink>(Assert.Single(p.Inlines, i => i is MdLink));
        Assert.Equal("https://example.com", link.Url);
    }

    [Fact]
    public void Image_BecomesLinkWithPlaceholderText()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("![一隻貓](https://example.com/cat.png)")));

        var link = Assert.IsType<MdLink>(Assert.Single(p.Inlines));
        Assert.Equal("[圖片：一隻貓]", Flat(link.Children));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/x", true)]
    [InlineData("mailto:a@b.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JAVASCRIPT:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("data:text/html;base64,AAAA", false)]
    [InlineData("ftp://x.test", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData("  https://padded.example  ", true)]
    public void LinkWhitelist(string url, bool allowed) => Assert.Equal(allowed, MarkdownLinks.IsAllowed(url));

    [Fact]
    public void Html_IsShownAsText_NotRendered()
    {
        var blocks = MarkdownParser.Parse("<div>hello</div>\n\n行內 <b>粗</b> 標籤");

        Assert.All(blocks, b => Assert.IsType<MdParagraph>(b));
        Assert.Contains("<div>hello</div>", Flat(((MdParagraph)blocks[0]).Inlines));
        Assert.Contains("<b>", Flat(((MdParagraph)blocks[1]).Inlines));
    }

    [Fact]
    public void Entities_AreDecoded()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("a &amp; b &lt; c")));

        Assert.Equal("a & b < c", Flat(p.Inlines));
    }

    [Fact]
    public void Source_IsEachTopLevelBlocksOwnText()
    {
        const string text = "# 標題\n\n第一段\n\n```\ncode\n```\n\n- a\n- b";

        var blocks = MarkdownParser.Parse(text);

        Assert.Equal("# 標題", blocks[0].Source);
        Assert.Equal("第一段", blocks[1].Source);
        Assert.StartsWith("```", blocks[2].Source);
        Assert.Contains("code", blocks[2].Source);
        Assert.Equal("- a\n- b", blocks[3].Source.Replace("\r", ""));
    }

    [Fact]
    public void Streaming_AppendingTextOnlyChangesTheLastBlocksSource()
    {
        var before = MarkdownParser.Parse("# 標題\n\n第一段\n\n第二段寫到一半");
        var after = MarkdownParser.Parse("# 標題\n\n第一段\n\n第二段寫到一半又多了一些");

        Assert.Equal(before[0].Source, after[0].Source);
        Assert.Equal(before[1].Source, after[1].Source);
        Assert.NotEqual(before[2].Source, after[2].Source);
    }

    [Fact]
    public void Emoji_StayInsideTextUntouched()
    {
        var p = Assert.IsType<MdParagraph>(Assert.Single(MarkdownParser.Parse("**❤️‍🔥 完整** 🇹🇼")));

        Assert.Equal("❤️‍🔥 完整 🇹🇼", Flat(p.Inlines));
    }
}
