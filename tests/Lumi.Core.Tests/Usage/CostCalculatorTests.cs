using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>M23 23B：費用公式（第 2 節）。</summary>
public sealed class CostCalculatorTests
{
    [Fact]
    public void FourTokenKinds_EachMultipliedByItsOwnPrice()
    {
        // 輸入 3、輸出 15、快取讀取 0.30、快取寫入 3.75（US$／每百萬 tokens）
        var price = PriceSnapshot.From(new ModelPricing(3m, 15m, 0.3m, 3.75m));
        var tokens = new TokenUsage(InputTokens: 1_000_000, OutputTokens: 100_000, CacheCreationInputTokens: 100_000, CacheReadInputTokens: 200_000);

        // 3 + 1.5 + 0.375 + 0.06
        Assert.Equal(4.935m, CostCalculator.Cost(tokens, price));
    }

    [Fact]
    public void CacheRates_FallBackToInputRate_WhenNotSet()
    {
        var price = PriceSnapshot.From(new ModelPricing(3m, 15m));
        var tokens = new TokenUsage(0, 0, CacheCreationInputTokens: 1_000_000, CacheReadInputTokens: 1_000_000);

        Assert.Equal(6m, CostCalculator.Cost(tokens, price));
    }

    [Fact]
    public void Free_CostIsZero_EvenWithTokens()
    {
        var price = PriceSnapshot.From(new ModelPricing(null, null, IsFree: true));

        Assert.Equal(0m, CostCalculator.Cost(new TokenUsage(500_000, 500_000, 1, 1), price));
    }

    [Fact]
    public void NoSnapshot_CostIsNull()
    {
        Assert.Null(CostCalculator.Cost(new TokenUsage(10, 10, 0, 0), null));
    }

    [Fact]
    public void ZeroTokens_CostIsZero()
    {
        var price = PriceSnapshot.From(new ModelPricing(3m, 15m));

        Assert.Equal(0m, CostCalculator.Cost(TokenUsage.Zero, price));
    }
}
