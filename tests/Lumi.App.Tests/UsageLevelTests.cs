using Lumi.App.ViewModels;

namespace Lumi.App.Tests;

/// <summary>用量等級門檻（ui-usage-and-topbar.md 2.1）：&lt;60% Normal、60%–85% 未滿 Warning、≥85% Danger；沒資料為 Empty。</summary>
public class UsageLevelTests
{
    [Fact]
    public void WithoutData_IsEmpty_RegardlessOfRatio()
    {
        Assert.Equal(UsageLevel.Empty, UsageLevelRules.From(false, 0));
        Assert.Equal(UsageLevel.Empty, UsageLevelRules.From(false, 0.9));
    }

    [Theory]
    [InlineData(0, UsageLevel.Normal)]
    [InlineData(0.59, UsageLevel.Normal)]
    [InlineData(0.60, UsageLevel.Warning)]
    [InlineData(0.84, UsageLevel.Warning)]
    [InlineData(0.85, UsageLevel.Danger)]
    [InlineData(1, UsageLevel.Danger)]
    public void WithData_MapsRatioToLevel(double ratio, UsageLevel expected)
    {
        Assert.Equal(expected, UsageLevelRules.From(true, ratio));
    }
}
