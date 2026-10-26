using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;

namespace Lumi.App.Tests;

/// <summary>本對話累計（ui-usage-and-topbar.md 第 4 節）：已存加總、進行中不重複計、完成後併入、切換歸零。</summary>
public sealed class ConversationUsageTallyTests
{
    [Fact]
    public void Load_SumsStoredAssistantUsages_AndSkipsMissingOnes()
    {
        var tally = new ConversationUsageTally();

        tally.Load([new TokenUsage(10, 5, 2, 3), null, new TokenUsage(1, 1, 0, 0)]);

        Assert.True(tally.HasData);
        Assert.Equal(new TokenUsage(11, 6, 2, 3), tally.Total);
        Assert.Equal(16, tally.TotalInputIncludingCache);   // 11 + 2 + 3
        Assert.Equal(3, tally.TotalCacheRead);
        Assert.Equal(6, tally.TotalOutput);
    }

    [Fact]
    public void Load_WithNothing_HasNoData()
    {
        var tally = new ConversationUsageTally();

        tally.Load([null, null]);

        Assert.False(tally.HasData);
        Assert.Equal(TokenUsage.Zero, tally.Total);
    }

    [Fact]
    public void InFlight_IsTheLatestCumulativeValue_NotAddedUp()
    {
        var tally = new ConversationUsageTally();
        tally.Load([]);

        // 串流中每個 UsageEvent 都是這次請求的累計值：只能取最後一個，不能相加
        tally.ReportInFlight(new TokenUsage(10, 2, 0, 0));
        tally.ReportInFlight(new TokenUsage(10, 5, 0, 0));
        tally.ReportInFlight(new TokenUsage(10, 7, 0, 0));

        Assert.True(tally.HasData);
        Assert.Equal(10, tally.TotalInputIncludingCache);
        Assert.Equal(7, tally.TotalOutput);
        Assert.Equal(new TokenUsage(10, 7, 0, 0), tally.InFlight);
    }

    [Fact]
    public void InFlight_AddsToCommitted_WhileStreaming()
    {
        var tally = new ConversationUsageTally();
        tally.Load([new TokenUsage(100, 20, 0, 50)]);

        tally.ReportInFlight(new TokenUsage(30, 4, 0, 0));

        // 已存：輸入 100 ＋ 快取讀取 50；進行中：輸入 30 → 含快取的總輸入 = 100 + 50 + 30
        Assert.Equal(180, tally.TotalInputIncludingCache);
        Assert.Equal(50, tally.TotalCacheRead);
        Assert.Equal(24, tally.TotalOutput);
    }

    [Fact]
    public void Complete_MergesTheMessageUsage_AndClearsInFlight()
    {
        var tally = new ConversationUsageTally();
        tally.Load([]);
        tally.ReportInFlight(new TokenUsage(10, 2, 0, 0));

        tally.Complete(new TokenUsage(10, 7, 0, 4));

        Assert.Null(tally.InFlight);
        Assert.Equal(new TokenUsage(10, 7, 0, 4), tally.Committed);
        Assert.Equal(7, tally.TotalOutput);
    }

    [Fact]
    public void Complete_FallsBackToTheInFlightValue_WhenTheMessageHasNoUsage()
    {
        var tally = new ConversationUsageTally();
        tally.Load([]);
        tally.ReportInFlight(new TokenUsage(8, 3, 0, 0));

        tally.Complete(null);

        Assert.Null(tally.InFlight);
        Assert.Equal(new TokenUsage(8, 3, 0, 0), tally.Committed);
    }

    [Fact]
    public void ToolLoop_EachRequestCountsOnce()
    {
        var tally = new ConversationUsageTally();
        tally.Load([]);

        // 一輪裡兩次請求（第一次要工具、第二次給答案）：兩次都併入，回合數由工作台另算
        tally.ReportInFlight(new TokenUsage(5, 3, 0, 0));
        tally.Complete(new TokenUsage(5, 3, 0, 0));
        tally.ReportInFlight(new TokenUsage(7, 4, 0, 0));
        tally.Complete(new TokenUsage(7, 4, 0, 0));

        Assert.Equal(12, tally.TotalInputIncludingCache);
        Assert.Equal(7, tally.TotalOutput);
    }

    [Fact]
    public void Abandon_DropsTheUnfinishedRequest_KeepsWhatWasCommitted()
    {
        var tally = new ConversationUsageTally();
        tally.Load([new TokenUsage(10, 5, 0, 0)]);
        tally.ReportInFlight(new TokenUsage(20, 9, 0, 0));

        tally.Abandon();

        Assert.Null(tally.InFlight);
        Assert.Equal(new TokenUsage(10, 5, 0, 0), tally.Total);
    }

    [Fact]
    public void Load_ReplacesEverything_SoSwitchingConversationsStartsFromTheStoredValues()
    {
        var tally = new ConversationUsageTally();
        tally.Load([new TokenUsage(100, 10, 0, 0)]);
        tally.ReportInFlight(new TokenUsage(5, 5, 0, 0));

        tally.Load([new TokenUsage(1, 1, 0, 0)]);

        Assert.Null(tally.InFlight);
        Assert.Equal(new TokenUsage(1, 1, 0, 0), tally.Total);
    }

    [Fact]
    public void Reset_ZeroesEverything_LikeANewConversation()
    {
        var tally = new ConversationUsageTally();
        tally.Load([new TokenUsage(100, 10, 0, 5)]);
        tally.ReportInFlight(new TokenUsage(5, 5, 0, 0));

        tally.Reset();

        Assert.False(tally.HasData);
        Assert.Null(tally.InFlight);
        Assert.Equal(TokenUsage.Zero, tally.Total);
    }

    [Theory]
    [InlineData(100, 0, 50, 150)]   // Anthropic：input_tokens 不含快取，輸入 = input + creation + read
    [InlineData(200, 800, 0, 1000)] // OpenAI 相容：prompt_tokens 含快取，轉換後 InputTokens = prompt − cached
    [InlineData(200, 300, 0, 500)]  // DeepSeek：prompt_cache_hit_tokens 含在 prompt_tokens 內
    public void InputIncludingCache_IsTheSameForEveryProvider(int inputTokens, int cacheRead, int cacheCreation, int expected)
    {
        var usage = new TokenUsage(inputTokens, 0, cacheCreation, cacheRead);

        Assert.Equal(expected, ConversationUsageTally.InputIncludingCache(usage));
    }
}
