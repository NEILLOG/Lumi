using Lumi.App.Markdown;

namespace Lumi.App.Tests.Markdown;

/// <summary>
/// 中文緊接粗體結尾的標點（FX-12）：「**重點：**內容」要解析成粗體，不能把 <c>**</c> 顯示出來。
/// 英數的粗體照常。
/// </summary>
public class MarkdownCjkEmphasisTests
{
    /// <summary>區塊裡所有行內元素（含清單、引用裡面的段落）。</summary>
    private static IEnumerable<MdInline> AllInlines(IEnumerable<MdBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdParagraph p: foreach (var i in p.Inlines) yield return i; break;
                case MdHeading h: foreach (var i in h.Inlines) yield return i; break;
                case MdQuote q: foreach (var i in AllInlines(q.Blocks)) yield return i; break;
                case MdList l:
                    foreach (var item in l.Items)
                        foreach (var i in AllInlines(item)) yield return i;
                    break;
            }
        }
    }

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

    [Theory]
    [InlineData("**重點：**內容", "重點：")]
    [InlineData("- **測試：**123", "測試：")]
    [InlineData("**「測試」**說明", "「測試」")]
    [InlineData("**測試(A)**說明", "測試(A)")]
    [InlineData("1. **步驟一：**打開設定", "步驟一：")]
    [InlineData("- **測試** 123", "測試")]
    public void BoldEndingNextToPunctuationAndBeforeChinese_IsBold(string markdown, string boldText)
    {
        var blocks = MarkdownParser.Parse(markdown);
        var inlines = AllInlines(blocks).ToList();

        var strong = inlines.OfType<MdStrong>().Single();
        Assert.Equal(boldText, Flat(strong.Children));
        Assert.DoesNotContain("**", Flat(inlines));   // 不能留下字面的星號
    }

    [Fact]
    public void AsciiBoldStillWorks()
    {
        var inlines = AllInlines(MarkdownParser.Parse("a**b**c")).ToList();

        Assert.Equal("b", Flat(inlines.OfType<MdStrong>().Single().Children));
    }

    [Fact]
    public void ClipboardHtml_CjkBoldBecomesStrong()
    {
        var html = MarkdownClipboard.ToHtml("**重點：**內容");

        Assert.Contains("<strong>重點：</strong>內容", html);
        Assert.DoesNotContain("**", html);
    }
}
