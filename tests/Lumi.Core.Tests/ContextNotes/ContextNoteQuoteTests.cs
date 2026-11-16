using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 Phase 0：「已決定」的原話拆分（D11）。</summary>
public class ContextNoteQuoteTests
{
    [Fact]
    public void WithAQuote_SplitsMainAndQuote()
    {
        var (main, quote) = ContextNoteQuote.Split("- 用 A 方案（使用者原話：「那就用 A」）");

        Assert.Equal("- 用 A 方案", main);
        Assert.Equal("那就用 A", quote);
    }

    [Fact]
    public void WithoutAQuote_ReturnsTheTextUnchanged()
    {
        var (main, quote) = ContextNoteQuote.Split("- 用 A 方案");

        Assert.Equal("- 用 A 方案", main);
        Assert.Null(quote);
    }

    [Fact]
    public void QuoteNotAtTheEndOfTheFirstLine_IsNotSplit()
    {
        const string text = "- 用 A 方案\n  - 細節（使用者原話：「好」）";
        var (main, quote) = ContextNoteQuote.Split(text);

        Assert.Equal(text, main);
        Assert.Null(quote);

        const string middle = "- 用 A（使用者原話：「好」）之後再看";
        Assert.Equal((middle, (string?)null), ContextNoteQuote.Split(middle));
    }

    [Fact]
    public void QuoteContainingCornerBrackets_IsKeptWhole()
    {
        var (main, quote) = ContextNoteQuote.Split("- 名稱定為研究本（使用者原話：「就叫「研究本」吧」）");

        Assert.Equal("- 名稱定為研究本", main);
        Assert.Equal("就叫「研究本」吧", quote);
    }

    [Fact]
    public void ChildLines_StayInTheMainText()
    {
        var (main, quote) = ContextNoteQuote.Split("- 用 A（使用者原話：「用 A」）  \n  - 理由：便宜");

        Assert.Equal("- 用 A\n  - 理由：便宜", main);
        Assert.Equal("用 A", quote);
    }
}
