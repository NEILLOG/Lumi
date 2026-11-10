using System.Text;
using Lumi.App.Markdown;

namespace Lumi.App.Tests.Markdown;

/// <summary>複製回答時放進剪貼簿的 HTML（<see cref="MarkdownClipboard"/>）。</summary>
public class MarkdownClipboardTests
{
    [Fact]
    public void ToHtml_KeepsFormatting()
    {
        var html = MarkdownClipboard.ToHtml("# 標題\n\n**粗體**、*斜體*、`code`\n\n- 一\n- 二");

        Assert.Contains("<h1", html);
        Assert.Contains("<strong>粗體</strong>", html);
        Assert.Contains("<em>斜體</em>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<li>一</li>", html);
    }

    [Fact]
    public void ToHtml_TablesGetBorders()
    {
        var html = MarkdownClipboard.ToHtml("| 欄 |\n|---|\n| 格 |");

        Assert.Contains("<table border=\"1\"", html);
        Assert.Contains("<td>格</td>", html);
    }

    [Fact]
    public void ToHtml_OnlyAllowedLinksStayLinks_AndImagesBecomeText()
    {
        var html = MarkdownClipboard.ToHtml("[好](https://example.com) [壞](javascript:alert(1)) ![圖](https://example.com/a.png)");

        Assert.Contains("<a href=\"https://example.com\">好</a>", html);
        Assert.DoesNotContain("javascript", html);
        Assert.Contains("壞", html);
        Assert.DoesNotContain("<img", html);   // 貼上時不去抓外部圖片
        Assert.Contains("[圖片：圖]", html);
    }

    [Fact]
    public void ToHtml_RawHtmlIsText()
    {
        var html = MarkdownClipboard.ToHtml("<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void ToCfHtml_OffsetsPointAtTheHtmlAndTheFragment()
    {
        const string fragment = "<p><strong>中文</strong> ✅</p>";
        var bytes = MarkdownClipboard.ToCfHtml(fragment);
        var text = Encoding.UTF8.GetString(bytes);

        int Offset(string name) => int.Parse(text.Split("\r\n").Single(l => l.StartsWith(name + ":", StringComparison.Ordinal))[(name.Length + 1)..]);
        string Slice(int from, int to) => Encoding.UTF8.GetString(bytes, from, to - from);

        Assert.StartsWith("Version:0.9\r\n", text);
        Assert.Equal(fragment, Slice(Offset("StartFragment"), Offset("EndFragment")));
        Assert.StartsWith("<html>", Slice(Offset("StartHTML"), Offset("EndHTML")));
        Assert.EndsWith("</html>", Slice(Offset("StartHTML"), Offset("EndHTML")));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
    }
}
