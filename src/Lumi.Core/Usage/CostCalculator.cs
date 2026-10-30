using Lumi.Core.Chat;

namespace Lumi.Core.Usage;

/// <summary>費用計算（M23）：用請求當時的單價快照算美元（純函式）。</summary>
public static class CostCalculator
{
    private const decimal TokensPerUnit = 1_000_000m;

    /// <summary>
    /// 這次請求的費用（US$）。單價快照是 null（當時未設定單價）回 null；免費回 0。
    /// 快取的單價在快照裡已經解析（沒有設定時就是輸入價），這裡直接乘。
    /// </summary>
    public static decimal? Cost(TokenUsage tokens, PriceSnapshot? price)
    {
        if (price is null)
            return null;
        if (price.IsFree)
            return 0m;
        var total = tokens.InputTokens * price.Input
                    + tokens.OutputTokens * price.Output
                    + tokens.CacheReadInputTokens * price.CacheRead
                    + tokens.CacheCreationInputTokens * price.CacheWrite;
        return total / TokensPerUnit;
    }
}
