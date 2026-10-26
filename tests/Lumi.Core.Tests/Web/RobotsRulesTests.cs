using Lumi.Core.Web;

namespace Lumi.Core.Tests.Web;

public class RobotsRulesTests
{
    private static bool Allowed(string robots, string url) =>
        RobotsRules.Parse(robots).IsAllowed(new Uri(url));

    [Fact]
    public void Empty_file_allows_everything()
    {
        Assert.Empty(RobotsRules.Parse("").Rules);
        Assert.True(Allowed("", "https://e.com/anything"));
    }

    [Fact]
    public void AllowAll_allows_everything() =>
        Assert.True(RobotsRules.AllowAll.IsAllowed(new Uri("https://e.com/private")));

    [Fact]
    public void Star_group_disallow_blocks_prefix()
    {
        const string robots = "User-agent: *\nDisallow: /private";
        Assert.False(Allowed(robots, "https://e.com/private/x"));
        Assert.True(Allowed(robots, "https://e.com/public"));
    }

    [Theory]
    [InlineData("User-agent: Lumi\nDisallow: /lumi")]
    [InlineData("user-agent: LUMI\ndisallow: /lumi")]
    public void Lumi_group_applies_case_insensitively(string robots)
    {
        Assert.False(Allowed(robots, "https://e.com/lumi/a"));
        Assert.True(Allowed(robots, "https://e.com/private"));
    }

    [Fact]
    public void Other_agents_are_ignored()
    {
        Assert.True(Allowed("User-agent: Googlebot\nDisallow: /", "https://e.com/"));
    }

    [Fact]
    public void Multiple_groups_only_keep_matching_ones()
    {
        const string robots = "User-agent: Googlebot\nDisallow: /g\n\nUser-agent: *\nDisallow: /a";
        Assert.True(Allowed(robots, "https://e.com/g"));
        Assert.False(Allowed(robots, "https://e.com/a"));
        Assert.Single(RobotsRules.Parse(robots).Rules);
    }

    [Fact]
    public void Consecutive_user_agent_lines_share_a_group()
    {
        const string robots = "User-agent: Googlebot\nUser-agent: *\nDisallow: /shared";
        Assert.False(Allowed(robots, "https://e.com/shared"));
    }

    [Fact]
    public void Group_ends_at_next_user_agent_line()
    {
        const string robots = "User-agent: *\nDisallow: /a\nUser-agent: Googlebot\nDisallow: /b";
        Assert.False(Allowed(robots, "https://e.com/a"));
        Assert.True(Allowed(robots, "https://e.com/b"));
    }

    [Fact]
    public void Empty_disallow_allows_everything()
    {
        const string robots = "User-agent: *\nDisallow:";
        Assert.Empty(RobotsRules.Parse(robots).Rules);
        Assert.True(Allowed(robots, "https://e.com/anything"));
    }

    [Fact]
    public void Disallow_root_blocks_everything()
    {
        Assert.False(Allowed("User-agent: *\nDisallow: /", "https://e.com/"));
        Assert.False(Allowed("User-agent: *\nDisallow: /", "https://e.com/deep/page"));
    }

    [Fact]
    public void Longer_allow_overrides_shorter_disallow()
    {
        const string robots = "User-agent: *\nDisallow: /a\nAllow: /a/b";
        Assert.True(Allowed(robots, "https://e.com/a/b/c"));
        Assert.False(Allowed(robots, "https://e.com/a/x"));
    }

    [Fact]
    public void Allow_wins_over_disallow_of_same_length()
    {
        const string robots = "User-agent: *\nDisallow: /p\nAllow: /p";
        Assert.True(Allowed(robots, "https://e.com/p/x"));
    }

    [Fact]
    public void Longest_match_wins_even_when_disallow()
    {
        const string robots = "User-agent: *\nDisallow: /a\nAllow: /a/b/\nDisallow: /a/b/c";
        Assert.False(Allowed(robots, "https://e.com/a/b/c/d"));
        Assert.True(Allowed(robots, "https://e.com/a/b/x"));
    }

    [Fact]
    public void Star_matches_any_run_of_characters()
    {
        const string robots = "User-agent: *\nDisallow: /search*q=";
        Assert.False(Allowed(robots, "https://e.com/search?q=1"));
        Assert.True(Allowed(robots, "https://e.com/search?x=1"));
    }

    [Fact]
    public void Dollar_anchors_end_of_path()
    {
        const string robots = "User-agent: *\nDisallow: /*.pdf$";
        Assert.False(Allowed(robots, "https://e.com/docs/a.pdf"));
        Assert.True(Allowed(robots, "https://e.com/docs/a.pdfx"));
        Assert.True(Allowed(robots, "https://e.com/docs/a.pdf?download=1"));
    }

    [Fact]
    public void Query_string_is_part_of_the_match()
    {
        const string robots = "User-agent: *\nDisallow: /q?x=";
        Assert.False(Allowed(robots, "https://e.com/q?x=1"));
        Assert.True(Allowed(robots, "https://e.com/q"));
    }

    [Fact]
    public void Regex_characters_in_rules_are_literal()
    {
        const string robots = "User-agent: *\nDisallow: /a+b(c)";
        Assert.False(Allowed(robots, "https://e.com/a+b(c)/x"));
        Assert.True(Allowed(robots, "https://e.com/aab"));
    }

    [Fact]
    public void Comments_are_ignored()
    {
        Assert.Empty(RobotsRules.Parse("# User-agent: *\n# Disallow: /x").Rules);
        Assert.False(Allowed("User-agent: * # all bots\nDisallow: /c # no c", "https://e.com/c/1"));
    }

    [Fact]
    public void Broken_lines_are_skipped()
    {
        const string robots = "User-agent: *\nthis is garbage\nDisallow /nocolon\nDisallow: /ok";
        Assert.True(Allowed(robots, "https://e.com/nocolon"));
        Assert.False(Allowed(robots, "https://e.com/ok"));
    }

    [Fact]
    public void Unknown_fields_do_not_break_the_group()
    {
        const string robots = "User-agent: *\nCrawl-delay: 5\nDisallow: /slow\nSitemap: https://e.com/s.xml";
        Assert.False(Allowed(robots, "https://e.com/slow"));
    }

    [Fact]
    public void Handles_windows_line_endings()
    {
        Assert.False(Allowed("User-agent: *\r\nDisallow: /w\r\n", "https://e.com/w/1"));
    }
}
