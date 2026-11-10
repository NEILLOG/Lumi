using Lumi.App.Markdown;

namespace Lumi.App.Tests.Markdown;

/// <summary>選取的範圍組回 Markdown 的行內格式（<see cref="MarkdownCopy.Inline"/>）。整份還原見 <c>MarkdownSelectionCopyTests</c>。</summary>
public class MarkdownCopyTests
{
    private static MarkdownSpan Plain(int start, string text) => new(start, text, false, false, false, false, null);

    [Fact]
    public void Inline_PartialSelection_KeepsOnlyTheSelectedPartWithItsFormat()
    {
        // 「前面粗體後面」：粗體是第 2～3 個字
        IReadOnlyList<MarkdownSpan> spans =
        [
            Plain(0, "前面"),
            new(2, "粗體", Bold: true, Italic: false, Strike: false, Code: false, Link: null),
            Plain(4, "後面"),
        ];

        Assert.Equal("面**粗體**", MarkdownCopy.Inline(spans, 1, 4));
        Assert.Equal("**粗**", MarkdownCopy.Inline(spans, 2, 3));
    }

    [Fact]
    public void Inline_WhitespaceStaysOutsideTheMarkers()
    {
        IReadOnlyList<MarkdownSpan> spans = [new(0, " 粗體 ", true, false, false, false, null)];

        Assert.Equal(" **粗體** ", MarkdownCopy.Inline(spans, 0, 4));
    }

    [Fact]
    public void Inline_MergesAdjacentRunsWithTheSameFormat()
    {
        // emoji 會把同一段粗體切成好幾個 Run
        IReadOnlyList<MarkdownSpan> spans =
        [
            new(0, "讚", true, false, false, false, null),
            new(1, "✅", true, false, false, false, null),
        ];

        Assert.Equal("**讚✅**", MarkdownCopy.Inline(spans, 0, 2));
    }

    [Fact]
    public void Inline_CodeWithBackticks_UsesALongerFence()
    {
        IReadOnlyList<MarkdownSpan> spans = [new(0, "a`b", false, false, false, true, null)];

        Assert.Equal("``a`b``", MarkdownCopy.Inline(spans, 0, 3));
    }

    [Fact]
    public void Inline_LinkWithMixedFormat_IsOneLink()
    {
        IReadOnlyList<MarkdownSpan> spans =
        [
            new(0, "說明", false, false, false, false, "https://example.com"),
            new(2, "文件", true, false, false, false, "https://example.com"),
        ];

        Assert.Equal("[說明**文件**](https://example.com)", MarkdownCopy.Inline(spans, 0, 4));
    }
}
