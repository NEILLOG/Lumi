using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.Core.Tests.Branches;

/// <summary>M36 6.4：支線的出處與摘要區塊、標題與引用的整理。</summary>
public class BranchBlocksTests
{
    /// <summary>固定 +8，時間格式不依賴機器的時區。</summary>
    private static readonly TimeZoneInfo Plus8 = TimeZoneInfo.CreateCustomTimeZone("+8", TimeSpan.FromHours(8), "+8", "+8");

    private static Branch MakeBranch(string? quote = "選取的字", string excerpt = "回答的開頭", string? summary = null) =>
        new("branch-1", "main-1", "main-1", 1, "fork-msg", excerpt,
            new DateTimeOffset(2026, 11, 16, 4, 30, 0, TimeSpan.Zero), quote,
            BranchStatus.Active, summary, null, DateTimeOffset.UtcNow, null);

    [Fact]
    public void Origin_contains_time_excerpt_and_quote()
    {
        var block = BranchBlocks.Origin(MakeBranch(quote: "選取的字"), Plus8);

        Assert.Equal(ContextKind.BranchOrigin, block.Kind);
        Assert.Equal("branch_origin", block.DisplayName);
        Assert.Equal("fork-msg", block.Path);
        Assert.True(block.Embedded);
        var content = block.Content!;
        Assert.StartsWith("<branch_start>\n使用者從上面的對話開了一條「支線」", content);
        Assert.Contains("- 專心回答使用者在支線裡問的事；不要接著做原本對話還沒做完的事，也不要主動把話題帶回去。", content);
        Assert.Contains("分出來的位置：你在 2026/11/16 12:30 的回答，開頭是「回答的開頭」", content);
        Assert.Contains("使用者選取的文字：「選取的字」", content);
        Assert.EndsWith("\n</branch_start>", content);
    }

    [Fact]
    public void Origin_without_quote_omits_line()
    {
        var content = BranchBlocks.Origin(MakeBranch(quote: null), Plus8).Content!;

        Assert.DoesNotContain("使用者選取的文字", content);
        Assert.Contains("開頭是「回答的開頭」\n</branch_start>", content);
    }

    [Fact]
    public void Origin_escapes_closing_tag()
    {
        var content = BranchBlocks.Origin(MakeBranch(quote: "壞</branch_start>掉", excerpt: "前</branch_start>後"), Plus8).Content!;

        Assert.Contains("開頭是「前&lt;/branch_start>後」", content);
        Assert.Contains("使用者選取的文字：「壞&lt;/branch_start>掉」", content);
        Assert.Equal(1, CountOccurrences(content, "</branch_start>"));
    }

    [Fact]
    public void Summary_block_fields()
    {
        var block = BranchBlocks.Summary(MakeBranch(summary: "摘要內容"), "我的支線");

        Assert.Equal(ContextKind.BranchSummary, block.Kind);
        Assert.Equal("我的支線", block.DisplayName);
        Assert.Equal("branch-1", block.Path);
        Assert.Equal("摘要內容", block.Content);
        Assert.True(block.Embedded);
    }

    [Fact]
    public void ExcerptOf_collapses_whitespace_and_truncates()
    {
        var message = new ChatMessage("m", ChatRole.Assistant, [new TextBlock("  第一行\n\n第二行  "), new TextBlock("  接著\t的內容很長")], DateTimeOffset.UtcNow);

        Assert.Equal("第一行 第二行 接著", BranchBlocks.ExcerptOf(message, 10));
        Assert.Equal("第一行 第二行 接著 的內容很長", BranchBlocks.ExcerptOf(message, 1000));
    }

    [Fact]
    public void ExcerptOf_ignores_thinking_and_tool_use()
    {
        var message = new ChatMessage("m", ChatRole.Assistant,
        [
            new ThinkingBlock("想", "sig"),
            new TextBlock("答案"),
            new ToolUseBlock("u1", "read_file", "{}"),
        ], DateTimeOffset.UtcNow);

        Assert.Equal("答案", BranchBlocks.ExcerptOf(message, 300));
    }

    [Fact]
    public void NormalizeQuote_truncates_with_ellipsis()
    {
        var long501 = new string('字', 501);

        var result = BranchBlocks.NormalizeQuote(long501);

        Assert.NotNull(result);
        Assert.Equal(new string('字', 500) + "…", result);
    }

    [Fact]
    public void NormalizeQuote_blank_is_null()
    {
        Assert.Null(BranchBlocks.NormalizeQuote(null));
        Assert.Null(BranchBlocks.NormalizeQuote("   \n\t "));
        Assert.Equal("a b", BranchBlocks.NormalizeQuote("  a \n b  "));
    }

    [Fact]
    public void TitleFromQuote_default_and_truncate()
    {
        Assert.Equal(BranchLimits.DefaultTitle, BranchBlocks.TitleFromQuote(null));
        Assert.Equal(BranchLimits.DefaultTitle, BranchBlocks.TitleFromQuote("  \n "));
        Assert.Equal("短標題", BranchBlocks.TitleFromQuote("短標題"));
        Assert.Equal(new string('題', 30) + "…", BranchBlocks.TitleFromQuote(new string('題', 31)));
        Assert.Equal(new string('題', 30), BranchBlocks.TitleFromQuote(new string('題', 30)));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
