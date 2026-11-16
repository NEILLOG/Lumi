using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.Core.Tests.Chat;

/// <summary>M36 D13：主線的摘要整理把範圍內的 <see cref="ContextKind.BranchSummary"/> 區塊接在摘要後面。</summary>
public class CompactionBranchSummaryTests
{
    private static ChatMessage Msg(int seq, ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, DateTimeOffset.UtcNow) { Seq = seq };

    private static ContextBlock BranchSummary(string title) =>
        new(ContextKind.BranchSummary, title, "branch-" + title, "摘要 " + title, true);

    [Fact]
    public void Summary_keeps_branch_summaries_from_compacted_range()
    {
        var history = new List<ChatMessage>
        {
            Msg(1, ChatRole.User, BranchSummary("S1"), new TextBlock("第一題")),
            Msg(2, ChatRole.Assistant, new TextBlock("答1")),
            Msg(3, ChatRole.User, BranchSummary("S2"), new TextBlock("第二題")),
            Msg(4, ChatRole.Assistant, new TextBlock("答2")),
            Msg(5, ChatRole.User, BranchSummary("S3"), new TextBlock("第三題")),
        };

        var applied = CompactionApplier.Apply(history, new CompactionState(null, 4, "使用者要查資料", 5));

        Assert.Equal(5, applied[0].Seq);
        var content = applied[0].Content;
        Assert.Equal(CompactionApplier.SummaryPrefix("使用者要查資料"), Assert.IsType<TextBlock>(content[0]).Text);
        // 範圍內的 S1、S2 接在摘要後面（依原本順序）；範圍外的 S3 原位
        Assert.Equal("S1", Assert.IsType<ContextBlock>(content[1]).DisplayName);
        Assert.Equal("S2", Assert.IsType<ContextBlock>(content[2]).DisplayName);
        Assert.Equal("S3", Assert.IsType<ContextBlock>(content[3]).DisplayName);
        Assert.Equal("第三題", Assert.IsType<TextBlock>(content[4]).Text);
        Assert.Equal(5, content.Count);
        Assert.DoesNotContain(applied, m => m.Seq <= 4);
    }

    [Fact]
    public void Without_branch_summaries_output_is_unchanged()
    {
        var history = new List<ChatMessage>
        {
            Msg(1, ChatRole.User, new TextBlock("第一題")),
            Msg(2, ChatRole.Assistant, new TextBlock("答1")),
            Msg(3, ChatRole.User, new TextBlock("第二題")),
            Msg(4, ChatRole.Assistant, new TextBlock("答2")),
            Msg(5, ChatRole.User, new TextBlock("第三題")),
        };

        var applied = CompactionApplier.Apply(history, new CompactionState(null, 4, "使用者要查資料", 5));

        // 和沒有支線區塊時的既有行為一樣：只在第一則使用者訊息前面加摘要，其餘內容不動
        Assert.Single(applied);
        Assert.Equal(2, applied[0].Content.Count);
        Assert.Equal(
            new TextBlock(CompactionApplier.SummaryPrefix("使用者要查資料")),
            applied[0].Content[0]);
        Assert.Equal(new TextBlock("第三題"), applied[0].Content[1]);
    }
}
