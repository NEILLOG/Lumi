using Lumi.Core.Chat;

namespace Lumi.Core.Branches;

/// <summary>一層的訊息與它的整理狀態（支線是 <see cref="CompactionState.Empty"/>）。</summary>
public sealed record BranchHistoryLayer(IReadOnlyList<ChatMessage> Messages, CompactionState Compaction);

/// <summary>支線的請求歷史（計畫 4.3）：上一層（與上上層）的訊息接上支線自己的。</summary>
public static class BranchHistory
{
    /// <summary>由上而下（主線在前）把每一層套用整理狀態後接起來（4.3 第 1 點）。</summary>
    public static IReadOnlyList<ChatMessage> Combine(IReadOnlyList<BranchHistoryLayer> layers)
    {
        var result = new List<ChatMessage>();
        foreach (var layer in layers)
            result.AddRange(CompactionApplier.Apply(layer.Messages, layer.Compaction));
        return result;
    }
}
