using System.Text;
using Lumi.Web.Content;

namespace Lumi.Web.Tests.Content;

public sealed class FeedReaderTests
{
    private readonly FeedReader _reader = new();

    private static byte[] Utf8(string xml) => Encoding.UTF8.GetBytes(xml);

    [Fact]
    public void Parses_rss_2()
    {
        var feed = _reader.Parse(Utf8("""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel>
              <title>範例新聞</title><link>https://example.com/</link>
              <item><title>第一則</title><link>https://example.com/1</link><pubDate>Mon, 05 Oct 2026 08:30:00 GMT</pubDate><description>摘要一</description></item>
              <item><title>第二則</title><link>https://example.com/2</link></item>
            </channel></rss>
            """));

        Assert.NotNull(feed);
        Assert.Equal("範例新聞", feed.Title);
        Assert.Equal(2, feed.Items.Count);
        Assert.Equal("第一則", feed.Items[0].Title);
        Assert.Equal("https://example.com/1", feed.Items[0].Link?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero), feed.Items[0].Published);
        Assert.Equal("摘要一", feed.Items[0].Summary);
        Assert.Null(feed.Items[1].Published);
        Assert.Equal("", feed.Items[1].Summary);
    }

    [Fact]
    public void Parses_atom()
    {
        var feed = _reader.Parse(Utf8("""
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom 範例</title><id>urn:x</id><updated>2026-10-01T00:00:00Z</updated>
              <link href="https://example.com/" />
              <entry><title>文章</title><id>urn:1</id><link href="https://example.com/a" />
                <updated>2026-10-02T12:00:00Z</updated><summary>內容摘要</summary></entry>
            </feed>
            """));

        Assert.NotNull(feed);
        Assert.Equal("Atom 範例", feed.Title);
        var item = Assert.Single(feed.Items);
        Assert.Equal("文章", item.Title);
        Assert.Equal("https://example.com/a", item.Link?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero), item.Published);
        Assert.Equal("內容摘要", item.Summary);
    }

    [Fact]
    public void Html_summary_is_stripped_and_truncated()
    {
        var longText = new string('字', 400);
        var feed = _reader.Parse(Utf8($"""
            <rss version="2.0"><channel><title>t</title>
              <item><title>a</title><description>&lt;p&gt;你好   &lt;b&gt;世界&lt;/b&gt;&lt;/p&gt;&lt;script&gt;x()&lt;/script&gt;</description></item>
              <item><title>b</title><description>&lt;p&gt;{longText}&lt;/p&gt;</description></item>
            </channel></rss>
            """));

        Assert.NotNull(feed);
        Assert.StartsWith("你好 世界", feed.Items[0].Summary);
        Assert.DoesNotContain("<", feed.Items[0].Summary);
        Assert.Equal(301, feed.Items[1].Summary.Length);
        Assert.EndsWith("…", feed.Items[1].Summary);
    }

    [Fact]
    public void Relative_item_link_is_resolved_against_first_feed_link()
    {
        var feed = _reader.Parse(Utf8("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>t</title><id>x</id><updated>2026-10-01T00:00:00Z</updated>
              <link href="https://example.com/blog/" />
              <entry><title>a</title><id>1</id><updated>2026-10-01T00:00:00Z</updated><link href="post/1" /></entry>
            </feed>
            """));

        Assert.NotNull(feed);
        Assert.Equal("https://example.com/blog/post/1", feed.Items[0].Link?.AbsoluteUri);
    }

    [Fact]
    public void Relative_item_link_without_base_is_null()
    {
        var feed = _reader.Parse(Utf8("""
            <rss version="2.0"><channel><title>t</title>
              <item><title>a</title><link>/post/1</link></item>
            </channel></rss>
            """));

        Assert.NotNull(feed);
        Assert.Null(feed.Items[0].Link);
    }

    [Fact]
    public void Missing_feed_title_gets_placeholder()
    {
        var feed = _reader.Parse(Utf8("""<rss version="2.0"><channel><item><title>a</title></item></channel></rss>"""));

        Assert.NotNull(feed);
        Assert.Equal("（沒有標題）", feed.Title);
    }

    [Fact]
    public void Html_page_is_not_a_feed()
    {
        Assert.Null(_reader.Parse(Utf8("<html><body><p>hello</p></body></html>")));
    }

    [Fact]
    public void Plain_text_and_empty_are_not_feeds()
    {
        Assert.Null(_reader.Parse(Utf8("just text")));
        Assert.Null(_reader.Parse([]));
    }

    [Fact]
    public void Other_xml_is_not_a_feed()
    {
        Assert.Null(_reader.Parse(Utf8("<?xml version=\"1.0\"?><data><x/></data>")));
    }

    [Fact]
    public void Xml_with_dtd_is_rejected_without_expanding_entities()
    {
        var feed = _reader.Parse(Utf8("""
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe "boom">]>
            <rss version="2.0"><channel><title>&xxe;</title><item><title>a</title></item></channel></rss>
            """));

        Assert.Null(feed);
    }
}
