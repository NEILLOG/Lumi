namespace Lumi.Tarot;

/// <summary>牌陣座標換算（plan/25 D9）。實機調整間距只改這裡的常數。</summary>
public static class TarotSpreadLayout
{
    /// <summary>牌高 / 牌寬。</summary>
    public const double CardAspect = 12.0 / 7.0;

    /// <summary>下限 1.357，否則橫放的交叉牌會蓋到左右兩張（D9）。</summary>
    public const double HorizontalGap = 1.45;

    public const double VerticalGap = 1.05;

    /// <summary>牌的中心點相對於牌陣中心的位置（像素）。x、y 是 layout 的值；cardWidth 是一張牌的寬度（像素）。</summary>
    public static (double X, double Y) Center(double x, double y, double cardWidth) =>
        (x * cardWidth * HorizontalGap, y * cardWidth * CardAspect * VerticalGap);
}
