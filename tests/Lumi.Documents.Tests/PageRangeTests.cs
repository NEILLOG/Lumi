namespace Lumi.Documents.Tests;

public class PageRangeTests
{
    [Theory]
    [InlineData("3", 10, 3, 3)]
    [InlineData("3-5", 10, 3, 5)]
    [InlineData(" 3-5 ", 10, 3, 5)]
    [InlineData("3 - 5", 10, 3, 5)]
    [InlineData("1", 1, 1, 1)]
    [InlineData("48-100", 60, 48, 60)]   // 結束頁超過總數時截到總數
    [InlineData("1-50", 200, 1, 50)]     // 剛好 50 頁
    public void Valid_ranges_are_parsed(string text, int max, int from, int to)
    {
        var ok = PageRange.TryParse(text, max, out var range, out var error);

        Assert.True(ok, error);
        Assert.Equal((from, to), range);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_asks_for_a_page(string? text)
    {
        Assert.False(PageRange.TryParse(text, 10, out _, out var error));
        Assert.Contains("請指定", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0-3")]
    [InlineData("-3")]
    [InlineData("a")]
    [InlineData("3-")]
    [InlineData("1-2-3")]
    [InlineData("+3")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void Malformed_or_out_of_domain_text_is_rejected(string text)
    {
        Assert.False(PageRange.TryParse(text, 10, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Start_after_the_last_page_is_out_of_range()
    {
        Assert.False(PageRange.TryParse("9", 8, out _, out var error));
        Assert.Contains("超出範圍", error);
    }

    [Fact]
    public void End_before_start_is_rejected()
    {
        Assert.False(PageRange.TryParse("5-3", 10, out _, out var error));
        Assert.Contains("不能小於", error);
    }

    [Fact]
    public void Span_over_fifty_is_rejected_even_after_clamping()
    {
        Assert.Equal(50, PageRange.MaxSpan);

        Assert.False(PageRange.TryParse("1-51", 200, out _, out var error));
        Assert.Contains("50", error);

        // 1-100 在 60 頁的文件裡，截到 60 之後跨度是 60，仍然超過上限
        Assert.False(PageRange.TryParse("1-100", 60, out _, out _));
    }
}
