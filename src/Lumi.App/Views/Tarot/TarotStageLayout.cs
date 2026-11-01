using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 牌陣畫布的版面（plan/25 25D；ui-spec 3.1）。純函式：牌的中心、旋轉後外框、位置名稱的位置、編號圓點的錨點都在這裡算，
/// 畫面只照擺，所以附件的外框寬度（<see cref="TarotAttachmentView"/> 判斷並排或改排）也用同一份數字。
/// </summary>
internal static class TarotStageLayout
{
    /// <summary>位置名稱與牌底的距離（ui-spec 3.1：8）。</summary>
    public const double CaptionGap = 8;

    /// <summary>位置名稱那一行的高度（字 11 放得下）。</summary>
    public const double CaptionHeight = 16;

    /// <summary>圓點的錨點：牌（旋轉後外框）左上角往外的距離（x−6、y−6）。</summary>
    public const double BadgeOutset = 6;

    /// <summary>
    /// 有編號時四周留白，讓往外 6 的圓點不被切掉（ui-spec 3.1）。高亮時圓點放大到 24 再加 2px 外圈，
    /// 比一般大小往外多約 5，所以留 12（6 + 5 再取整）。
    /// </summary>
    public const double NumberedGutter = 12;

    /// <summary>
    /// 一張牌的版面（畫布座標，已扣掉最小值並加上留白）。<see cref="BoundsLeft"/>、<see cref="BoundsTop"/> 是旋轉後外框的左上角，
    /// 圓點的錨點由它推得；<see cref="CaptionLeft"/>、<see cref="CaptionTop"/> 是位置名稱行的左上角。
    /// </summary>
    public sealed record Placement(
        TarotPlacedCard Card,
        int Number,
        double CenterX,
        double CenterY,
        double BoundsLeft,
        double BoundsTop,
        double CaptionLeft,
        double CaptionTop);

    public sealed record Layout(double Width, double Height, double CaptionWidth, IReadOnlyList<Placement> Cards);

    /// <summary>位置名稱行的寬上限 = 牌寬 × 橫向間距（三張 96 → 139.2）。</summary>
    public static double CaptionWidth(double cardWidth) => cardWidth * TarotSpreadLayout.HorizontalGap;

    public static Layout Compute(
        IReadOnlyList<TarotPlacedCard> cards, double cardWidth, TarotArrangement arrangement, bool showNumbers, bool showCaptions)
    {
        var captionWidth = CaptionWidth(cardWidth);
        if (cards.Count == 0)
            return new Layout(0, 0, captionWidth, []);

        var centers = TarotSpreadLayout.Centers(
            cards.Select(card => (card.X, card.Y, card.Rotation)).ToList(), cardWidth, arrangement == TarotArrangement.CompactCeltic);
        var height = TarotSpreadLayout.CardHeight(cardWidth);

        // 每張牌的旋轉後外框，以及（有位置名稱時）名稱行的範圍，一起算出整個畫布的範圍
        var boxes = new (double Left, double Top, double Right, double Bottom)[cards.Count];
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        for (var i = 0; i < cards.Count; i++)
        {
            var (width, boxHeight) = TarotSpreadLayout.RotatedBounds(cardWidth, cards[i].Rotation);
            var (cx, cy) = centers[i];
            boxes[i] = (cx - width / 2, cy - boxHeight / 2, cx + width / 2, cy + boxHeight / 2);
            left = Math.Min(left, boxes[i].Left);
            top = Math.Min(top, boxes[i].Top);
            right = Math.Max(right, boxes[i].Right);
            bottom = Math.Max(bottom, boxes[i].Bottom);
            if (showCaptions)
            {
                left = Math.Min(left, cx - captionWidth / 2);
                right = Math.Max(right, cx + captionWidth / 2);
                bottom = Math.Max(bottom, boxes[i].Bottom + CaptionGap + CaptionHeight);
            }
        }

        var gutter = showNumbers ? NumberedGutter : 0;
        var placements = new List<Placement>(cards.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            var (cx, cy) = centers[i];
            var canvasCenterX = cx - left + gutter;
            var canvasBoundsBottom = boxes[i].Bottom - top + gutter;
            placements.Add(new Placement(
                cards[i],
                i + 1,
                canvasCenterX,
                cy - top + gutter,
                boxes[i].Left - left + gutter,
                boxes[i].Top - top + gutter,
                canvasCenterX - captionWidth / 2,
                canvasBoundsBottom + CaptionGap));
        }

        return new Layout(right - left + 2 * gutter, bottom - top + 2 * gutter, captionWidth, placements);
    }

    /// <summary>圓點的中心（畫布座標）。中心固定，高亮時圓點變大也不會移動。</summary>
    public static (double X, double Y) BadgeCenter(Placement placement)
    {
        var (width, height) = TarotNumberBadge.SizeOf(placement.Number, highlighted: false);
        return (placement.BoundsLeft - BadgeOutset + width / 2, placement.BoundsTop - BadgeOutset + height / 2);
    }
}
