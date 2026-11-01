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

/// <summary>25D：凱爾特十字的縮小版排法（右側直排靠近十字，ui-spec 3.1）。</summary>
public sealed class TarotSpreadLayoutCompactTests
{
    private const double Tolerance = 0.001;

    /// <summary>凱爾特十字的 layout（x 牌寬、y 牌高、rotation 度數），順序同 spreads.json。</summary>
    private static readonly (double X, double Y, double Rotation)[] Celtic =
    [
        (0, 0, 0), (0, 0, 90), (0, -1, 0), (0, 1, 0), (1, 0, 0),
        (-1, 0, 0), (3, 1.5, 0), (3, 0.5, 0), (3, -0.5, 0), (3, -1.5, 0),
    ];

    [Fact]
    public void Centers_WithoutCompactColumn_MatchEveryCardCenter()
    {
        var centers = TarotSpreadLayout.Centers(Celtic, 56, compactColumn: false);

        for (var i = 0; i < Celtic.Length; i++)
        {
            var expected = TarotSpreadLayout.Center(Celtic[i].X, Celtic[i].Y, 56);
            Assert.Equal(expected.X, centers[i].X, Tolerance);
            Assert.Equal(expected.Y, centers[i].Y, Tolerance);
        }
    }

    [Fact]
    public void Centers_CompactColumn_PlacesTheRightColumnGapAfterTheCrossBoundingBox()
    {
        var centers = TarotSpreadLayout.Centers(Celtic, 56, compactColumn: true);

        // 十字最右緣 = 右臂中心 81.2 + 半寬 28 = 109.2（交叉牌旋轉後外框的寬是 96，半寬 48，不會超過右臂）
        var expectedX = 81.2 + 28 + TarotSpreadLayout.CompactColumnGap + 28;
        Assert.Equal(153.2, expectedX, Tolerance);
        for (var i = 6; i < 10; i++)
        {
            Assert.Equal(expectedX, centers[i].X, Tolerance);
            Assert.Equal(TarotSpreadLayout.Center(Celtic[i].X, Celtic[i].Y, 56).Y, centers[i].Y, Tolerance);
        }

        // 十字的牌照原座標
        for (var i = 0; i < 6; i++)
            Assert.Equal(TarotSpreadLayout.Center(Celtic[i].X, Celtic[i].Y, 56).X, centers[i].X, Tolerance);
    }

    [Fact]
    public void Extent_CelticCrossAtWidth56_IsAbout291By398()
    {
        var (width, height) = TarotSpreadLayout.Extent(Celtic, 56, compactColumn: true);

        Assert.InRange(width, 290, 292);
        Assert.InRange(height, 397, 399);
    }

    [Fact]
    public void Compact_WithoutAColumnToTheRight_MatchesTheOriginalArrangement()
    {
        // 最大 x = 1（只有十字）時不是直排，應與原本的座標完全相同
        var cross = Celtic[..6];

        var compact = TarotSpreadLayout.Centers(cross, 56, compactColumn: true);
        var original = TarotSpreadLayout.Centers(cross, 56, compactColumn: false);

        for (var i = 0; i < cross.Length; i++)
        {
            Assert.Equal(original[i].X, compact[i].X, Tolerance);
            Assert.Equal(original[i].Y, compact[i].Y, Tolerance);
        }
    }

    [Fact]
    public void RotatedBounds_NinetyDegrees_SwapsWidthAndHeight()
    {
        var (width, height) = TarotSpreadLayout.RotatedBounds(56, 90);

        Assert.Equal(TarotSpreadLayout.CardHeight(56), width, Tolerance);
        Assert.Equal(56, height, Tolerance);
    }
}
