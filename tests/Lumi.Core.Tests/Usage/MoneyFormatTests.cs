using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>費用與 tokens 的顯示格式。Tokens 的數字與 App 的 <c>WorkbenchViewModel.FormatTokens</c> 對照。</summary>
public sealed class MoneyFormatTests
{
    // 1 美元 = 32 新台幣（Money.TwdPerUsd）。下面用新台幣反推美元，確保換算後的金額剛好是要的數字。
    [Theory]
    [InlineData("0", "NT$0")]
    [InlineData("0.001", "NT$0")]
    [InlineData("0.1", "NT$3.2")]
    [InlineData("0.01", "NT$0.3")]
    [InlineData("0.09375", "NT$3")]
    [InlineData("0.3121875", "NT$10")]
    [InlineData("0.3125", "NT$10")]
    [InlineData("38.578125", "NT$1,235")]
    [InlineData("15.25", "NT$488")]
    [InlineData("25", "NT$800")]
    public void Twd_FormatsWholeDollarsBelowTenWithOneDecimal(string usd, string expected)
    {
        Assert.Equal(expected, MoneyFormat.Twd(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Twd_EstimatedAddsApproxPrefix()
    {
        Assert.Equal("≈NT$488", MoneyFormat.Twd(15.25m, estimated: true));
        Assert.Equal("NT$488", MoneyFormat.Twd(15.25m));
    }

    [Fact]
    public void Usd_KeepsTwoDecimals_WithThousandsSeparator()
    {
        Assert.Equal("US$15.25", MoneyFormat.Usd(15.25m));
        Assert.Equal("US$1,234.50", MoneyFormat.Usd(1234.5m));
        Assert.Equal("US$0.00", MoneyFormat.Usd(0m));
        Assert.Equal("US$0.01", MoneyFormat.Usd(0.005m));
    }

    [Fact]
    public void TwdAmount_RoundsToWholeDollars()
    {
        Assert.Equal(488m, MoneyFormat.TwdAmount(15.25m));
        Assert.Equal(3m, MoneyFormat.TwdAmount(0.1m));
    }

    [Fact]
    public void UsdFromTwd_DividesByRate()
    {
        Assert.Equal(25m, MoneyFormat.UsdFromTwd(800m));
    }

    // 數字與 App 的 WorkbenchViewModel.FormatTokens 一致（999／1000／92_400 是 plan 指定的邊界）。
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(1_500, "1.5K")]
    [InlineData(92_400, "92K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(12_000_000, "12M")]
    public void Tokens_MatchesWorkbenchFormatTokens(int tokens, string expected)
    {
        Assert.Equal(expected, MoneyFormat.Tokens(tokens));
    }
}
