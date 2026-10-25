using Lumi.Core.Mcp;

namespace Lumi.Core.Tests.Mcp;

public class McpToolNamingTests
{
    [Fact]
    public void ToolName_JoinsKeyAndToolWithDoubleUnderscores()
    {
        Assert.Equal("mcp__github__get_issue", McpToolNaming.ToolName("github", "get_issue"));
    }

    [Fact]
    public void ToolName_ReplacesCharactersOutsideTheAllowedSet()
    {
        Assert.Equal("mcp__kb__search_docs_v2_", McpToolNaming.ToolName("kb", "search docs.v2/"));
    }

    [Fact]
    public void ToolName_KeepsAnExactly64CharacterName()
    {
        var tool = new string('a', 64 - "mcp__k__".Length);

        var name = McpToolNaming.ToolName("k", tool);

        Assert.Equal(64, name.Length);
        Assert.EndsWith(tool, name);
    }

    [Fact]
    public void ToolName_TruncatesLongNamesAndAppendsAStableHash()
    {
        var tool = new string('a', 80);

        var first = McpToolNaming.ToolName("server", tool);
        var second = McpToolNaming.ToolName("server", tool);
        var other = McpToolNaming.ToolName("server", tool + "b");

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", first);
        Assert.Matches("_[0-9a-f]{8}$", first);
    }

    [Theory]
    [InlineData("GitHub", "github")]
    [InlineData("My Server!", "my-server")]
    [InlineData("  --a--b--  ", "a-b")]
    [InlineData("內部知識庫", "server")]
    [InlineData("", "server")]
    [InlineData("averyveryverylongservername", "averyveryverylon")]
    public void KeyFromName_NormalizesTheName(string name, string expected)
    {
        Assert.Equal(expected, McpToolNaming.KeyFromName(name, []));
    }

    [Fact]
    public void KeyFromName_AddsASuffixWhenTheKeyIsTaken()
    {
        Assert.Equal("github-2", McpToolNaming.KeyFromName("GitHub", ["github"]));
        Assert.Equal("github-3", McpToolNaming.KeyFromName("GitHub", ["github", "github-2"]));
    }

    [Fact]
    public void KeyFromName_KeepsTheSuffixedKeyWithinTheLengthLimit()
    {
        var long16 = new string('a', 16);

        var key = McpToolNaming.KeyFromName(long16, [long16]);

        Assert.Equal(16, key.Length);
        Assert.EndsWith("-2", key);
        Assert.True(McpToolNaming.IsValidKey(key));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("github", true)]
    [InlineData("a-b", true)]
    [InlineData("aaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaa", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("A", false)]
    [InlineData("a_b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidKey_FollowsTheRule(string? key, bool expected)
    {
        Assert.Equal(expected, McpToolNaming.IsValidKey(key));
    }
}
