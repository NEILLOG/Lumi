using Lumi.Core.Web;

namespace Lumi.Core.Tests.Web;

public class CrawlRulesTests
{
    private static Uri U(string text) => new(text);

    [Theory]
    [InlineData("https://Example.com/a?x=1#frag", "https://example.com/a?x=1")]
    [InlineData("https://example.com:443/", "https://example.com/")]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")]
    [InlineData("http://example.com:443/", "http://example.com:443/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/A/B", "https://example.com/A/B")]
    [InlineData("https://e.com/s?q=a&b=2#top", "https://e.com/s?q=a&b=2")]
    public void Normalize_removes_fragment_lowercases_host_and_default_port(string input, string expected) =>
        Assert.Equal(expected, CrawlRules.Normalize(U(input))!.AbsoluteUri);

    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///C:/a.html")]
    public void Normalize_returns_null_for_non_http(string input) =>
        Assert.Null(CrawlRules.Normalize(U(input)));

    [Fact]
    public void SameSite_ignores_case_and_default_port()
    {
        Assert.True(CrawlRules.SameSite(U("https://example.com"), U("HTTPS://EXAMPLE.com/x")));
        Assert.True(CrawlRules.SameSite(U("https://a.example"), U("https://a.example:443/y")));
    }

    [Fact]
    public void SameSite_rejects_different_scheme_port_or_host()
    {
        Assert.False(CrawlRules.SameSite(U("http://example.com/"), U("https://example.com/")));
        Assert.False(CrawlRules.SameSite(U("http://a.example:8080/"), U("http://a.example:8081/")));
        Assert.False(CrawlRules.SameSite(U("https://a.example/"), U("https://b.example/")));
        Assert.False(CrawlRules.SameSite(U("https://a.example/"), U("https://sub.a.example/")));
    }

    [Fact]
    public void PassesFilters_without_filters_allows_everything() =>
        Assert.True(CrawlRules.PassesFilters(U("https://e.com/any"), [], []));

    [Fact]
    public void PassesFilters_include_requires_one_match_case_insensitively()
    {
        Assert.True(CrawlRules.PassesFilters(U("https://e.com/docs/x"), ["/docs/"], []));
        Assert.True(CrawlRules.PassesFilters(U("https://e.com/docs/x"), ["/DOCS/"], []));
        Assert.True(CrawlRules.PassesFilters(U("https://e.com/blog/y"), ["/docs/", "/blog/"], []));
        Assert.False(CrawlRules.PassesFilters(U("https://e.com/blog/y"), ["/docs/"], []));
    }

    [Fact]
    public void PassesFilters_exclude_rejects_any_match()
    {
        Assert.False(CrawlRules.PassesFilters(U("https://e.com/ARCHIVE/1"), [], ["/archive/"]));
        Assert.True(CrawlRules.PassesFilters(U("https://e.com/docs/1"), [], ["/archive/"]));
        Assert.False(CrawlRules.PassesFilters(U("https://e.com/docs/draft-1"), ["/docs/"], ["draft"]));
    }

    [Fact]
    public void PassesFilters_matches_chinese_text_in_url()
    {
        Assert.True(CrawlRules.PassesFilters(U("https://e.com/文件/一"), ["文件"], []));
        Assert.False(CrawlRules.PassesFilters(U("https://e.com/文件/一"), [], ["文件"]));
    }

    [Fact]
    public void PassesFilters_ignores_blank_patterns()
    {
        Assert.True(CrawlRules.PassesFilters(U("https://e.com/x"), [" "], [""]));
        Assert.False(CrawlRules.PassesFilters(U("https://e.com/x"), ["  "], ["x"]));
    }

    [Theory]
    [InlineData("https://e.com/logout")]
    [InlineData("https://e.com/user/SignOut")]
    [InlineData("https://e.com/sign-out")]
    [InlineData("https://e.com/log-out")]
    [InlineData("https://e.com/logoff")]
    [InlineData("https://e.com/登出")]
    public void IsLogoutLink_detects_keywords_in_url(string input) =>
        Assert.True(CrawlRules.IsLogoutLink(U(input), null));

    [Theory]
    [InlineData("登出")]
    [InlineData("LOG-OUT ")]
    [InlineData("LOGOUT")]
    public void IsLogoutLink_detects_keywords_in_link_text(string text)
    {
        Assert.True(CrawlRules.IsLogoutLink(U("https://e.com/x"), text));
    }

    [Fact]
    public void IsLogoutLink_false_for_normal_links()
    {
        Assert.False(CrawlRules.IsLogoutLink(U("https://e.com/login"), "登入"));
        Assert.False(CrawlRules.IsLogoutLink(U("https://e.com/docs/about"), "關於"));
        Assert.False(CrawlRules.IsLogoutLink(U("https://e.com/docs"), null));
    }

    [Theory]
    [InlineData(1, "首頁", "001-首頁.md")]
    [InlineData(12, null, "012-page.md")]
    [InlineData(1, "   ", "001-page.md")]
    [InlineData(3, "...", "003-page.md")]
    [InlineData(100, "Title", "100-Title.md")]
    [InlineData(1, "a/b\\c:d*e?f\"g<h>i|j", "001-a_b_c_d_e_f_g_h_i_j.md")]
    [InlineData(1, "a\tb\nc", "001-a_b_c.md")]
    [InlineData(1, "a   b  \t c", "001-a b _ c.md")]
    [InlineData(1, "Title..  ", "001-Title.md")]
    [InlineData(1, "  padded  ", "001-padded.md")]
    public void PageFileName_sanitizes_title(int index, string? title, string expected) =>
        Assert.Equal(expected, CrawlRules.PageFileName(index, title));

    [Fact]
    public void PageFileName_truncates_to_40_characters()
    {
        var title = new string('a', 50);
        Assert.Equal("001-" + new string('a', 40) + ".md", CrawlRules.PageFileName(1, title));

        var chinese = new string('字', 45);
        Assert.Equal("001-" + new string('字', 40) + ".md", CrawlRules.PageFileName(1, chinese));
    }

    [Fact]
    public void PageFileName_keeps_emoji_whole()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 45));
        var expected = string.Concat(Enumerable.Repeat("😀", 40));
        Assert.Equal("001-" + expected + ".md", CrawlRules.PageFileName(1, emoji));
    }

    [Fact]
    public void PageFileName_strips_trailing_dot_after_truncation()
    {
        var title = new string('a', 39) + ".b.c";
        Assert.Equal("001-" + new string('a', 39) + ".md", CrawlRules.PageFileName(1, title));
    }

    [Fact]
    public void FolderName_uses_lowercase_host_and_local_time()
    {
        var now = new DateTimeOffset(2026, 10, 26, 9, 5, 7, TimeSpan.FromHours(8));
        Assert.Equal("docs.example.com-20261026-090507", CrawlRules.FolderName(U("https://Docs.Example.com:8443/x"), now));
    }

    [Fact]
    public void FolderName_replaces_colon_in_host()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var name = CrawlRules.FolderName(U("http://[::1]:8080/"), now);
        Assert.DoesNotContain(':', name);
        Assert.EndsWith("-20260102-030405", name);
    }

    [Theory]
    [InlineData("https://a.example/files/report.pdf", true)]
    [InlineData("https://a.example/files/REPORT.PDF", true)]
    [InlineData("https://a.example/a.docx?download=1", true)]
    [InlineData("https://a.example/img/logo.PNG", true)]
    [InlineData("https://a.example/docs/page.html", false)]
    [InlineData("https://a.example/docs/page", false)]
    [InlineData("https://a.example/v1.2/docs", false)]
    [InlineData("https://a.example/", false)]
    [InlineData("https://a.example/index.php", false)]
    public void LooksLikeFile_goes_by_the_extension_of_the_path(string url, bool expected) =>
        Assert.Equal(expected, CrawlRules.LooksLikeFile(new Uri(url)));
}
