using Lumi.Core.Browser;

namespace Lumi.Core.Tests.Browser;

public class BrowserRulesTests
{
    [Theory]
    [InlineData("enter", "Enter")]
    [InlineData("  ARROWDOWN ", "ArrowDown")]
    [InlineData("Escape", "Escape")]
    [InlineData("pagedown", "PageDown")]
    [InlineData("space", "Space")]
    public void Keys_are_normalized(string raw, string expected)
    {
        Assert.True(BrowserRules.TryParseKey(raw, out var key, out var error));
        Assert.Equal(expected, key);
        Assert.Equal("", error);
    }

    [Fact]
    public void AllowedKeys_has_the_fourteen_planned_keys()
    {
        Assert.Equal(14, BrowserRules.AllowedKeys.Length);
        Assert.All(BrowserRules.AllowedKeys, k => Assert.True(BrowserRules.TryParseKey(k, out var normalized, out _) && normalized == k));
    }

    [Fact]
    public void Unknown_key_lists_the_allowed_ones()
    {
        Assert.False(BrowserRules.TryParseKey("Control+A", out _, out var error));
        Assert.StartsWith("不支援按鍵「Control+A」，可用：Enter、Tab、", error);
        Assert.EndsWith("、Space。", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_key_is_missing(string? raw)
    {
        Assert.False(BrowserRules.TryParseKey(raw, out _, out var error));
        Assert.Equal("缺少 key（要按的鍵）。", error);
    }

    [Theory]
    [InlineData("up", BrowserScrollDirection.Up)]
    [InlineData("DOWN", BrowserScrollDirection.Down)]
    [InlineData(" Top ", BrowserScrollDirection.Top)]
    [InlineData("bottom", BrowserScrollDirection.Bottom)]
    [InlineData(null, BrowserScrollDirection.Down)]
    [InlineData("  ", BrowserScrollDirection.Down)]
    public void Directions_parse_and_default_to_down(string? raw, BrowserScrollDirection expected)
    {
        Assert.True(BrowserRules.TryParseDirection(raw, out var direction, out var error));
        Assert.Equal(expected, direction);
        Assert.Equal("", error);
    }

    [Fact]
    public void Unknown_direction_is_rejected()
    {
        Assert.False(BrowserRules.TryParseDirection("left", out _, out var error));
        Assert.Equal("direction 只能是 up、down、top 或 bottom。", error);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("   ", null)]
    public void Wait_needs_at_least_one_condition(string? url, string? text)
    {
        var error = BrowserRules.ValidateWait(url, text, null, out _, out _);

        Assert.Equal("請用 done_when_url_contains 或 done_when_text 指定「完成」的條件，Lumi 才知道什麼時候可以繼續。", error);
    }

    [Fact]
    public void Wait_with_url_only_is_valid_and_trimmed()
    {
        var error = BrowserRules.ValidateWait("  /dashboard ", "   ", null, out var condition, out var timeout);

        Assert.Null(error);
        Assert.Equal("/dashboard", condition.UrlContains);
        Assert.Null(condition.TextVisible);
        Assert.Equal(TimeSpan.FromSeconds(180), timeout);
    }

    [Fact]
    public void Wait_with_text_only_is_valid()
    {
        var error = BrowserRules.ValidateWait(null, " 歡迎回來 ", 30, out var condition, out var timeout);

        Assert.Null(error);
        Assert.Null(condition.UrlContains);
        Assert.Equal("歡迎回來", condition.TextVisible);
        Assert.Equal(TimeSpan.FromSeconds(30), timeout);
    }

    [Fact]
    public void Wait_with_both_conditions_is_valid()
    {
        var error = BrowserRules.ValidateWait("/home", "登出", null, out var condition, out _);

        Assert.Null(error);
        Assert.Equal(new BrowserWaitCondition("/home", "登出"), condition);
    }

    [Fact]
    public void Wait_url_longer_than_200_is_rejected_but_exactly_200_is_fine()
    {
        Assert.Equal("done_when_url_contains 太長（超過 200 字）。",
            BrowserRules.ValidateWait(new string('a', 201), null, null, out _, out _));
        Assert.Null(BrowserRules.ValidateWait(new string('a', 200), null, null, out _, out _));
    }

    [Fact]
    public void Wait_text_longer_than_100_is_rejected_but_exactly_100_is_fine()
    {
        Assert.Equal("done_when_text 太長（超過 100 字）。",
            BrowserRules.ValidateWait(null, new string('字', 101), null, out _, out _));
        Assert.Null(BrowserRules.ValidateWait(null, new string('字', 100), null, out _, out _));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(300, 300)]
    [InlineData(600, 600)]
    [InlineData(5000, 600)]
    [InlineData(-5, 10)]
    public void Wait_timeout_is_clamped(int requested, int expectedSeconds)
    {
        BrowserRules.ValidateWait("x", null, requested, out _, out var timeout);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }

    [Theory]
    [InlineData(null, 12_000)]
    [InlineData(500, 2_000)]
    [InlineData(2_000, 2_000)]
    [InlineData(20_000, 20_000)]
    [InlineData(99_999, 30_000)]
    public void Snapshot_chars_default_and_clamp(int? value, int expected)
    {
        Assert.Equal(expected, BrowserRules.TryParseSnapshotChars(value));
    }

    [Fact]
    public void Blocked_download_extensions_match_download_file()
    {
        Assert.Equal(
            [".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".sh", ".command", ".app", ".pkg", ".dmg"],
            BrowserRules.BlockedDownloadExtensions);
    }

    [Theory]
    [InlineData(".exe", true)]
    [InlineData(".MSI", true)]
    [InlineData(".Dmg", true)]
    [InlineData(".pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBlockedDownloadExtension_ignores_case(string? extension, bool expected) =>
        Assert.Equal(expected, BrowserRules.IsBlockedDownloadExtension(extension));
}
