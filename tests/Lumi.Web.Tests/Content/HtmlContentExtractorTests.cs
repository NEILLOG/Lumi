using System.Text;
using Lumi.Web.Content;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Web.Tests.Content;

public sealed class HtmlContentExtractorTests
{
    private static readonly Uri Base = new("https://example.com/news/story.html");

    private readonly HtmlContentExtractor _extractor = new(NullLogger<HtmlContentExtractor>.Instance);

    private static string Paragraphs(int count) => string.Concat(Enumerable.Range(1, count).Select(i =>
        $"<p>這是第 {i} 段文章內容，描述了一件值得閱讀的事情，句子要夠長才會被判定為主要內容，因此我們多寫幾個字，" +
        "讓可讀性演算法認為這裡就是文章本體，並且包含逗號、句號與足夠的文字數量。</p>"));

    private static string ArticlePage(string extraBody = "") => $"""
        <!DOCTYPE html>
        <html><head><title>頁面標題 - 範例網站</title><script>var tracking = 1;</script><style>/* css-marker */</style></head>
        <body>
          <nav><ul><li><a href="/">首頁</a></li><li><a href="/about">關於</a></li></ul></nav>
          <article>
            <h1>文章標題</h1>
            {Paragraphs(12)}
            <p>延伸閱讀：<a href="/related/page">相關文章</a></p>
            {extraBody}
          </article>
          <footer><a href="https://other.example/x#top">其他網站</a></footer>
        </body></html>
        """;

    [Fact]
    public void Article_page_uses_readability_and_drops_navigation()
    {
        var page = _extractor.Extract(ArticlePage(), Base, readable: true);

        Assert.True(page.UsedReadability);
        Assert.Contains("這是第 1 段文章內容", page.Markdown);
        Assert.Contains("這是第 12 段文章內容", page.Markdown);
        Assert.DoesNotContain("tracking", page.Markdown);
        Assert.DoesNotContain("css-marker", page.Markdown);
        Assert.DoesNotContain("首頁", page.Markdown);
        Assert.Contains("(https://example.com/related/page)", page.Markdown);
    }

    [Fact]
    public void Short_page_falls_back_to_whole_page()
    {
        var page = _extractor.Extract("<html><head><title>短</title></head><body><p>只有一句話。</p><a href='/x'>連結</a></body></html>", Base, readable: true);

        Assert.False(page.UsedReadability);
        Assert.Contains("只有一句話。", page.Markdown);
        Assert.Contains("[連結](https://example.com/x)", page.Markdown);
        Assert.Equal("短", page.Title);
    }

    [Fact]
    public void Readable_false_keeps_the_whole_page()
    {
        var page = _extractor.Extract(ArticlePage(), Base, readable: false);

        Assert.False(page.UsedReadability);
        Assert.Contains("首頁", page.Markdown);
        Assert.Contains("這是第 1 段文章內容", page.Markdown);
        Assert.Equal("頁面標題 - 範例網站", page.Title);
    }

    [Fact]
    public void Links_in_markdown_are_absolute()
    {
        var page = _extractor.Extract(ArticlePage(), Base, readable: false);

        Assert.Contains("(https://example.com/related/page)", page.Markdown);
        Assert.Contains("(https://example.com/about)", page.Markdown);
        Assert.DoesNotContain("](/", page.Markdown);
    }

    [Fact]
    public void Images_become_their_alt_text_or_disappear()
    {
        var page = _extractor.Extract(
            "<body><p>前 <img src='/a.png' alt='一張貓的照片'> 中 <img src='/b.png'> 後</p></body>", Base, readable: false);

        Assert.Contains("一張貓的照片", page.Markdown);
        Assert.DoesNotContain("a.png", page.Markdown);
        Assert.DoesNotContain("b.png", page.Markdown);
        Assert.DoesNotContain("![", page.Markdown);
    }

    [Fact]
    public void Script_style_and_friends_are_removed_from_whole_page()
    {
        var page = _extractor.Extract(
            "<body><p>正文</p><script>alert(1)</script><style>.x{}</style><noscript>請開 JS</noscript><template>範本</template>" +
            "<svg><text>圖形</text></svg><iframe src='/f'>框架</iframe><object>物件</object><embed src='/e'></body>",
            Base, readable: false);

        Assert.Equal("正文", page.Markdown);
    }

    [Fact]
    public void Link_list_is_deduplicated_absolute_and_in_order()
    {
        const string html = """
            <body>
              <a href="/a">甲</a>
              <a href="https://example.com/b#section">乙</a>
              <a href="/a#again">重複</a>
              <a href="/b"> </a>
              <a href="mailto:x@example.com">信</a>
              <a href="javascript:void(0)">腳本</a>
              <a href="/c"><span>多</span>   <span>段</span></a>
              <a href="/empty"></a>
            </body>
            """;

        var page = _extractor.Extract(html, Base, readable: false);

        Assert.Equal(
            ["https://example.com/a", "https://example.com/b", "https://example.com/c", "https://example.com/empty"],
            page.Links.Select(l => l.Url.AbsoluteUri));
        Assert.Equal("甲", page.Links[0].Text);
        Assert.Equal("乙", page.Links[1].Text);
        Assert.Equal("多 段", page.Links[2].Text);
        Assert.Equal("https://example.com/empty", page.Links[3].Text);
    }

    [Fact]
    public void Link_text_is_limited_to_200_chars()
    {
        var page = _extractor.Extract($"<a href='/long'>{new string('字', 500)}</a>", Base, readable: false);

        Assert.Equal(200, page.Links[0].Text.Length);
    }

    [Fact]
    public void Link_list_contains_whole_page_even_when_readability_is_used()
    {
        var page = _extractor.Extract(ArticlePage(), Base, readable: true);

        Assert.Contains(page.Links, l => l.Url.AbsoluteUri == "https://example.com/about");
        Assert.Contains(page.Links, l => l.Url.AbsoluteUri == "https://other.example/x");
    }

    [Fact]
    public void Missing_title_is_null()
    {
        var page = _extractor.Extract("<body><p>x</p></body>", Base, readable: false);

        Assert.Null(page.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<<<>>>")]
    [InlineData("<div><p>沒有結尾 <b>粗體 <a href='/x'>連結")]
    [InlineData("<table><tr><td>壞<tr></table></div></span>")]
    [InlineData("not html at all")]
    public void Broken_html_does_not_throw(string html)
    {
        var page = _extractor.Extract(html, Base, readable: true);

        Assert.NotNull(page);
        Assert.NotNull(page.Markdown);
    }

    [Fact]
    public void Decode_text_delegates_to_text_decoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding("big5").GetBytes("<meta charset=big5>繁體");

        Assert.Contains("繁體", _extractor.DecodeText(bytes, null, isHtml: true));
    }
}
