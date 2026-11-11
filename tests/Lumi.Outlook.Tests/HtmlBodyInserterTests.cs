namespace Lumi.Outlook.Tests;

public class HtmlBodyInserterTests
{
    [Fact]
    public void Html_insert_after_body_tag()
    {
        var result = HtmlBodyInserter.Insert("<html><BODY class=\"x\"><p>原信</p></body></html>", "回覆");

        var bodyEnd = result.IndexOf("<BODY class=\"x\">", StringComparison.Ordinal) + "<BODY class=\"x\">".Length;
        var pStart = result.IndexOf("<p>原信", StringComparison.Ordinal);
        Assert.True(bodyEnd > 0);
        Assert.True(pStart > bodyEnd);
        Assert.Equal("<div>回覆</div><br>", result.Substring(bodyEnd, pStart - bodyEnd));
    }

    [Fact]
    public void Html_insert_escapes_and_breaks_lines()
    {
        var result = HtmlBodyInserter.Insert("<html><body><p>原信</p></body></html>", "a<b>\n第二行");

        Assert.Contains("a&lt;b&gt;<br>第二行", result);
        Assert.DoesNotContain("<b>", result);
    }

    [Fact]
    public void Html_without_body_tag()
    {
        var result = HtmlBodyInserter.Insert("<p>原信</p>", "前面");

        Assert.Equal("<div>前面</div><br><p>原信</p>", result);
    }

    [Fact]
    public void Empty_html_returns_only_the_inserted_block()
    {
        Assert.Equal("<div>內容</div><br>", HtmlBodyInserter.Insert(null!, "內容"));
        Assert.Equal("<div>內容</div><br>", HtmlBodyInserter.Insert("", "內容"));
    }
}
