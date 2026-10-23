using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.Chat;

public class SearchTextTests
{
    [Fact]
    public void Extract_JoinsTextBlocks_WithNewlines()
    {
        var text = SearchTextExtractor.Extract([new TextBlock("第一段"), new TextBlock("第二段")]);

        Assert.Equal("第一段\n第二段", text);
    }

    [Fact]
    public void Extract_IgnoresEverythingExceptText()
    {
        var content = new ContentBlock[]
        {
            new ThinkingBlock("想很久的內容", "sig"),
            new ToolUseBlock("toolu_1", "read_file", """{"path":"a"}"""),
            new ToolResultBlock("toolu_1", "工具結果內容", false, "讀檔", null, null, 3, ToolApproval.NotRequired),
            new ContextBlock(ContextKind.File, "a.txt", "/a.txt", "檔案快照內容", true),
            new ImageBlock("abc.png", "image/png", "截圖"),
            new ProviderBlock("anthropic", "fallback", "{}"),
            new TextBlock("只有這段會被搜尋"),
        };

        Assert.Equal("只有這段會被搜尋", SearchTextExtractor.Extract(content));
    }

    [Fact]
    public void Extract_WithoutText_IsEmpty()
    {
        Assert.Equal(string.Empty, SearchTextExtractor.Extract([new ImageBlock("abc.png", "image/png", "截圖")]));
        Assert.Equal(string.Empty, SearchTextExtractor.Extract([]));
    }

    [Fact]
    public void Extract_TruncatesAtMaxLength_WithoutThrowing()
    {
        var big = new TextBlock(new string('字', SearchTextExtractor.MaxLength + 5_000));

        var text = SearchTextExtractor.Extract([big, new TextBlock("後面的")]);

        Assert.Equal(SearchTextExtractor.MaxLength, text.Length);
    }

    [Fact]
    public void Extract_TruncationDoesNotLeaveAHalfSurrogatePair()
    {
        // 第 20,000 個字元正好是 emoji 的高代理字元：截斷時要一起去掉，不能留下半個字
        var content = new TextBlock(new string('a', SearchTextExtractor.MaxLength - 1) + "😀");

        var text = SearchTextExtractor.Extract([content]);

        Assert.Equal(SearchTextExtractor.MaxLength - 1, text.Length);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }

    [Fact]
    public void Snippet_KeepsAboutTwentyCharactersOnEachSide()
    {
        var text = new string('甲', 50) + "報告" + new string('乙', 50);

        var snippet = SearchSnippet.Create(text, "報告");

        Assert.Equal(new string('甲', SearchSnippet.Context) + "報告" + new string('乙', SearchSnippet.Context), snippet);
    }

    [Fact]
    public void Snippet_AtTheStart_HasNoLeadingPart()
    {
        Assert.Equal("開頭的報告內容", SearchSnippet.Create("開頭的報告內容", "報告"));
        Assert.Equal("報告在最前面", SearchSnippet.Create("報告在最前面", "報告"));
    }

    [Fact]
    public void Snippet_IsCaseInsensitive_AndCollapsesWhitespace()
    {
        var snippet = SearchSnippet.Create("first line\n\n  Hello World\r\nlast", "hello world");

        Assert.Equal("first line Hello World last", snippet);
    }

    [Fact]
    public void Snippet_WhenQueryIsNotFound_TakesTheBeginning()
    {
        Assert.Equal("開頭", SearchSnippet.Create("開頭", "不存在"));
        Assert.Equal(string.Empty, SearchSnippet.Create(string.Empty, "x"));
    }
}
