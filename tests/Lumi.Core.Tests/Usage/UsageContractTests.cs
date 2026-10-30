using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>M23 Phase 0 的共用契約：單價快照、設定正規化。</summary>
public sealed class UsageContractTests
{
    [Fact]
    public void Snapshot_IsNull_WhenPriceNotSet()
    {
        Assert.Null(PriceSnapshot.From(ModelPricing.None));
        Assert.Null(PriceSnapshot.From(new ModelPricing(3m, null)));
        Assert.Null(PriceSnapshot.From(new ModelPricing(null, 15m)));
    }

    [Fact]
    public void Snapshot_CacheRatesFallBackToInputRate()
    {
        var snapshot = PriceSnapshot.From(new ModelPricing(3m, 15m));

        Assert.Equal(new PriceSnapshot(3m, 15m, 3m, 3m, false), snapshot);
    }

    [Fact]
    public void Snapshot_KeepsExplicitCacheRates()
    {
        var snapshot = PriceSnapshot.From(new ModelPricing(3m, 15m, 0.3m, 3.75m));

        Assert.Equal(new PriceSnapshot(3m, 15m, 0.3m, 3.75m, false), snapshot);
    }

    [Fact]
    public void Snapshot_Free_IsAllZero()
    {
        var snapshot = PriceSnapshot.From(new ModelPricing(null, null, IsFree: true));

        Assert.Equal(new PriceSnapshot(0, 0, 0, 0, true), snapshot);
    }

    [Fact]
    public void IsSet_NeedsBothRatesOrFree()
    {
        Assert.False(ModelPricing.None.IsSet);
        Assert.False(new ModelPricing(1m, null).IsSet);
        Assert.True(new ModelPricing(1m, 2m).IsSet);
        Assert.True(new ModelPricing(null, null, IsFree: true).IsSet);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Settings_NonPositiveBudget_BecomesNull(int budget)
    {
        Assert.Null(new UsageSettings(budget).Normalize().MonthlyBudgetUsd);
    }

    [Fact]
    public void Settings_KeepsPositiveBudget()
    {
        Assert.Equal(25m, new UsageSettings(25m).Normalize().MonthlyBudgetUsd);
    }

    [Fact]
    public void Settings_MutedHints_AreDeduplicatedAndFiltered()
    {
        var settings = new UsageSettings(null, [UsageHintIds.BigFile, "unknown", UsageHintIds.BigFile, UsageHintIds.Images]).Normalize();

        Assert.Equal([UsageHintIds.BigFile, UsageHintIds.Images], settings.Muted);
    }

    [Fact]
    public void HintIds_AreInPriorityOrder_AndUnique()
    {
        Assert.Equal(9, UsageHintIds.All.Count);
        Assert.Equal(UsageHintIds.All.Count, UsageHintIds.All.Distinct().Count());
        Assert.Equal(UsageHintIds.ModelSwitch, UsageHintIds.All[0]);
        Assert.Equal(UsageHintIds.RepeatPaste, UsageHintIds.All[^1]);
    }
}
