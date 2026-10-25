using Lumi.Core.Browser;

namespace Lumi.Core.Tests.Browser;

public class BrowserSnapshotParserTests
{
    private static readonly Uri Page = new("https://example.com/a/b/page.html?x=1#top");

    /// <summary>計畫撰寫時實測的快照（含 iframe 內的 f1e2）。</summary>
    private const string Fixture = """
        - generic [active] [ref=e1]:
          - heading "表單" [level=1] [ref=e2]
          - link "首頁" [ref=e3] [cursor=pointer]:
            - /url: /home#section
          - link "上一層" [ref=e4] [cursor=pointer]:
            - /url: ../up/index.html
          - button "送出" [ref=e5]
          - textbox [ref=e6]
          - iframe [ref=e9]:
            - generic [ref=f1e1]:
              - button "框內按鈕" [ref=f1e2]
        """;

    private static ParsedSnapshot Parse(string raw, int maxChars = 12_000) => BrowserSnapshotParser.Parse(raw, Page, maxChars);

    [Fact]
    public void Relative_links_become_absolute_and_fragments_are_removed()
    {
        var result = Parse(Fixture);

        Assert.Contains("- /url: https://example.com/home", result.Text);
        Assert.DoesNotContain("#section", result.Text);
        Assert.Contains("- /url: https://example.com/a/up/index.html", result.Text);
        Assert.DoesNotContain("../up", result.Text);
    }

    [Fact]
    public void Absolute_links_keep_query_and_lose_fragment()
    {
        var result = Parse("- link \"x\" [ref=e1]:\n  - /url: https://other.org/p?q=1&r=2#frag");

        Assert.Contains("  - /url: https://other.org/p?q=1&r=2", result.Text);
        Assert.DoesNotContain("#frag", result.Text);
    }

    [Fact]
    public void Protocol_relative_links_use_the_page_scheme()
    {
        var result = Parse("- link \"x\" [ref=e1]:\n  - /url: //cdn.example.net/x");

        Assert.Contains("- /url: https://cdn.example.net/x", result.Text);
    }

    [Theory]
    [InlineData("javascript:void(0)")]
    [InlineData("mailto:a@b.com")]
    [InlineData("tel:+886123")]
    [InlineData("data:text/html,hi")]
    [InlineData("http://[bad")]
    public void Unsafe_or_unparseable_link_lines_are_removed(string url)
    {
        var result = Parse($"- link \"x\" [ref=e1]:\n  - /url: {url}\n- button \"y\" [ref=e2]");

        Assert.DoesNotContain("/url", result.Text);
        Assert.Contains("button \"y\"", result.Text);
        Assert.Equal(2, result.RefCount);
    }

    [Fact]
    public void Ref_labels_cover_named_unnamed_and_iframe_elements()
    {
        var result = Parse(Fixture);

        Assert.Equal("heading \"表單\"", result.RefLabels["e2"]);
        Assert.Equal("button \"送出\"", result.RefLabels["e5"]);
        Assert.Equal("textbox", result.RefLabels["e6"]);
        Assert.Equal("iframe", result.RefLabels["e9"]);
        Assert.Equal("button \"框內按鈕\"", result.RefLabels["f1e2"]);
        Assert.Equal("generic", result.RefLabels["e1"]);
    }

    [Fact]
    public void RefCount_equals_label_count_when_not_truncated()
    {
        var result = Parse(Fixture);

        Assert.Equal(result.RefLabels.Count, result.RefCount);
        Assert.Equal(9, result.RefCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Long_names_are_cut_to_60_chars_with_ellipsis()
    {
        var name = new string('字', 80);
        var result = Parse($"- button \"{name}\" [ref=e1]");

        Assert.Equal($"button \"{new string('字', 60)}…\"", result.RefLabels["e1"]);
    }

    [Fact]
    public void Name_with_escaped_quote_is_matched()
    {
        var result = Parse("- button \"say \\\"hi\\\"\" [ref=e1]");

        Assert.Equal("button \"say \\\"hi\\\"\"", result.RefLabels["e1"]);
    }

    [Fact]
    public void Duplicate_refs_keep_the_first_label()
    {
        var result = Parse("- button \"第一個\" [ref=e1]\n- link \"第二個\" [ref=e1]");

        Assert.Equal("button \"第一個\"", result.RefLabels["e1"]);
        Assert.Single(result.RefLabels);
        Assert.Equal(1, result.RefCount);
    }

    [Fact]
    public void Truncation_happens_at_a_line_break_and_sets_flag()
    {
        var raw = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"- button \"按鈕{i:D3}\" [ref=e{i}]"));

        var result = Parse(raw, 2_000);

        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= 2_000);
        Assert.EndsWith("]", result.Text);
        Assert.All(result.Text.Split('\n'), line => Assert.StartsWith("- button", line));
    }

    [Fact]
    public void Label_table_keeps_refs_from_the_truncated_part()
    {
        var raw = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"- button \"按鈕{i:D3}\" [ref=e{i}]"));

        var result = Parse(raw, 2_000);

        Assert.Equal(400, result.RefLabels.Count);
        Assert.Equal("button \"按鈕400\"", result.RefLabels["e400"]);
        Assert.True(result.RefCount < 400);
        Assert.Equal(result.Text.Split('\n').Length, result.RefCount);
    }

    [Fact]
    public void Text_without_line_breaks_is_cut_at_the_limit()
    {
        var result = Parse(new string('a', 5_000), 2_000);

        Assert.True(result.Truncated);
        Assert.Equal(2_000, result.Text.Length);
    }

    [Fact]
    public void Text_exactly_at_the_limit_is_not_truncated()
    {
        var result = Parse(new string('a', 2_000), 2_000);

        Assert.False(result.Truncated);
        Assert.Equal(2_000, result.Text.Length);
    }

    [Theory]
    [InlineData(10, 2_000)]
    [InlineData(100_000, 30_000)]
    public void MaxChars_is_clamped(int maxChars, int effective)
    {
        var raw = string.Join('\n', Enumerable.Range(1, 6_000).Select(i => $"- button \"按鈕{i:D4}\" [ref=e{i}]"));

        var result = Parse(raw, maxChars);

        Assert.True(result.Truncated);
        Assert.InRange(result.Text.Length, effective - 40, effective);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void Blank_snapshot_gives_empty_result(string raw)
    {
        var result = Parse(raw);

        Assert.Equal("", result.Text);
        Assert.Empty(result.RefLabels);
        Assert.Equal(0, result.RefCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Windows_line_endings_are_normalized()
    {
        var result = Parse(Fixture.Replace("\n", "\r\n"));

        Assert.DoesNotContain('\r', result.Text);
        Assert.Equal(9, result.RefCount);
        Assert.Contains("- /url: https://example.com/home", result.Text);
    }

    [Fact]
    public void Lines_without_refs_are_kept()
    {
        var result = Parse("- paragraph: 你好\n- button \"ok\" [ref=e1]");

        Assert.Contains("- paragraph: 你好", result.Text);
        Assert.Equal(1, result.RefCount);
    }
}
