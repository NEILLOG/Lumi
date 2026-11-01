namespace Lumi.Tarot.Tests;

public sealed class TarotSpreadLayoutTests
{
    private const double Tolerance = 0.001;

    [Fact]
    public void Center_HorizontalUsesCardWidthAndHorizontalGap()
    {
        var (x, y) = TarotSpreadLayout.Center(1, 0, 56);

        Assert.Equal(56 * TarotSpreadLayout.HorizontalGap, x, Tolerance);
        Assert.Equal(0, y, Tolerance);
    }

    [Fact]
    public void Center_VerticalUsesCardHeightAndVerticalGap()
    {
        var (x, y) = TarotSpreadLayout.Center(0, 1, 56);

        Assert.Equal(0, x, Tolerance);
        Assert.Equal(56 * TarotSpreadLayout.CardAspect * TarotSpreadLayout.VerticalGap, y, Tolerance);
    }

    [Fact]
    public void HorizontalGap_KeepsCrossingCardClearOfNeighbours()
    {
        // 交叉牌橫放後的半寬 = 牌高 / 2 = CardAspect / 2；左右兩張的內緣在 HorizontalGap - 0.5
        Assert.True(TarotSpreadLayout.HorizontalGap - 0.5 > TarotSpreadLayout.CardAspect / 2);
    }
}
