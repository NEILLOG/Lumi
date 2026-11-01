namespace Lumi.Tarot;

/// <summary>牌陣座標換算（plan/25 D9）。實機調整間距只改這裡的常數。</summary>
public static class TarotSpreadLayout
{
    /// <summary>牌高 / 牌寬。</summary>
    public const double CardAspect = 12.0 / 7.0;

    /// <summary>下限 1.357，否則橫放的交叉牌會蓋到左右兩張（D9）。</summary>
    public const double HorizontalGap = 1.45;

    public const double VerticalGap = 1.05;

    /// <summary>凱爾特十字縮小版：十字最右緣（旋轉後外框）與右側直排之間的空隙（像素，ui-spec 3.1）。</summary>
    public const double CompactColumnGap = 16;

    /// <summary>牌的中心點相對於牌陣中心的位置（像素）。x、y 是 layout 的值；cardWidth 是一張牌的寬度（像素）。</summary>
    public static (double X, double Y) Center(double x, double y, double cardWidth) =>
        (x * cardWidth * HorizontalGap, y * cardWidth * CardAspect * VerticalGap);

    /// <summary>牌高（像素）= 牌寬 × 12/7。</summary>
    public static double CardHeight(double cardWidth) => cardWidth * CardAspect;

    /// <summary>旋轉後外框的寬高（像素）；90° 時寬高互換。</summary>
    public static (double Width, double Height) RotatedBounds(double cardWidth, double rotation)
    {
        var height = CardHeight(cardWidth);
        var radians = rotation * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        return (cardWidth * cos + height * sin, cardWidth * sin + height * cos);
    }

    /// <summary>
    /// 每張牌的中心點（相對於牌陣中心，像素）。<paramref name="compactColumn"/> 為 true（凱爾特十字縮小版）時，
    /// x 等於所有牌最大值且最大值 &gt; 1 的牌是右側直排：中心 x = 其餘牌（十字）旋轉後外框的最右緣 + <see cref="CompactColumnGap"/> + 牌寬/2；
    /// 其他牌照 <see cref="Center"/>。
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Centers(
        IReadOnlyList<(double X, double Y, double Rotation)> cards, double cardWidth, bool compactColumn)
    {
        var centers = cards.Select(card => Center(card.X, card.Y, cardWidth)).ToList();
        if (!compactColumn || cards.Count == 0)
            return centers;

        var columnX = cards.Max(card => card.X);
        if (columnX <= 1)
            return centers;

        var crossRight = double.NegativeInfinity;
        for (var i = 0; i < cards.Count; i++)
        {
            if (cards[i].X == columnX)
                continue;
            var (width, _) = RotatedBounds(cardWidth, cards[i].Rotation);
            crossRight = Math.Max(crossRight, centers[i].X + width / 2);
        }
        if (double.IsNegativeInfinity(crossRight))
            return centers;

        var x = crossRight + CompactColumnGap + cardWidth / 2;
        for (var i = 0; i < cards.Count; i++)
        {
            if (cards[i].X == columnX)
                centers[i] = (x, centers[i].Y);
        }
        return centers;
    }

    /// <summary>整個牌陣（旋轉後的外框）的寬高（像素），不含位置名稱與圓點。</summary>
    public static (double Width, double Height) Extent(
        IReadOnlyList<(double X, double Y, double Rotation)> cards, double cardWidth, bool compactColumn)
    {
        if (cards.Count == 0)
            return (0, 0);

        var centers = Centers(cards, cardWidth, compactColumn);
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        for (var i = 0; i < cards.Count; i++)
        {
            var (width, height) = RotatedBounds(cardWidth, cards[i].Rotation);
            left = Math.Min(left, centers[i].X - width / 2);
            top = Math.Min(top, centers[i].Y - height / 2);
            right = Math.Max(right, centers[i].X + width / 2);
            bottom = Math.Max(bottom, centers[i].Y + height / 2);
        }
        return (right - left, bottom - top);
    }
}
