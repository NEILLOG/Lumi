using Lumi.Core.Branches;
using Lumi.Core.Chat;

namespace Lumi.Core.Tests.Branches;

/// <summary>M36 4.3 第 1 點：支線的請求歷史（上一層套用整理、接上支線自己的）。</summary>
public class BranchHistoryTests
{
    private static ChatMessage Msg(int seq, ChatRole role, string text) =>
        new(ChatMessage.NewId(), role, [new TextBlock(text)], DateTimeOffset.UtcNow) { Seq = seq };

    [Fact]
    public void Combine_concatenates_in_order()
    {
        IReadOnlyList<BranchHistoryLayer> layers =
        [
            new BranchHistoryLayer([Msg(1, ChatRole.User, "主線問"), Msg(2, ChatRole.Assistant, "主線答")], CompactionState.Empty),
            new BranchHistoryLayer([Msg(1, ChatRole.User, "支線問"), Msg(2, ChatRole.Assistant, "支線答")], CompactionState.Empty),
        ];

        var result = BranchHistory.Combine(layers);

        Assert.Equal(["主線問", "主線答", "支線問", "支線答"], result.Select(m => ((TextBlock)m.Content[0]).Text));
    }

    [Fact]
    public void Combine_applies_root_compaction_only_to_root_layer()
    {
        var root = new[] { Msg(1, ChatRole.User, "舊問"), Msg(2, ChatRole.Assistant, "舊答"), Msg(3, ChatRole.User, "新問") };
        var branch = new[] { Msg(1, ChatRole.User, "支線問") };
        IReadOnlyList<BranchHistoryLayer> layers =
        [
            new BranchHistoryLayer(root, new CompactionState(null, 2, "主線摘要")),
            new BranchHistoryLayer(branch, CompactionState.Empty),
        ];

        var result = BranchHistory.Combine(layers);

        // 主線：seq 1、2 拿掉，第一則使用者訊息前面加摘要
        var first = Assert.IsType<TextBlock>(result[0].Content[0]);
        Assert.StartsWith("<conversation_summary>", first.Text);
        Assert.Contains("主線摘要", first.Text);
        Assert.Equal("新問", Assert.IsType<TextBlock>(result[0].Content[1]).Text);
        // 支線那層原樣（不套用主線的摘要）
        Assert.Equal(2, result.Count);
        Assert.Equal("支線問", Assert.IsType<TextBlock>(result[1].Content[0]).Text);
    }

    [Fact]
    public void Combine_keeps_turn_suffix()
    {
        var user = Msg(1, ChatRole.User, "問") with { TurnSuffix = "模式指示" };

        var result = BranchHistory.Combine([new BranchHistoryLayer([user], CompactionState.Empty)]);

        Assert.Equal("模式指示", Assert.Single(result).TurnSuffix);
        Assert.Equal(1, Assert.Single(result).Seq);
    }

    [Fact]
    public void Combine_empty()
    {
        Assert.Empty(BranchHistory.Combine([]));
        Assert.Empty(BranchHistory.Combine([new BranchHistoryLayer([], CompactionState.Empty)]));
    }
}
