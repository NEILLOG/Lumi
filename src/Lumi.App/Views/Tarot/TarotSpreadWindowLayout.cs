using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 凱爾特十字牌陣視窗的版面數字（plan/25 25F；ui-spec 3.1）。純函式，牌寬隨可用空間等比縮放，測試直接呼叫。
/// </summary>
internal static class TarotSpreadWindowLayout
{
    /// <summary>預設視窗（600 × 760）的牌寬（ui-spec 3.1）。</summary>
    public const double DefaultCardWidth = 72;

    /// <summary>牌寬下限。</summary>
    public const double MinCardWidth = 40;

    /// <summary>牌寬上限。</summary>
    public const double MaxCardWidth = 160;

    /// <summary>牌寬變動要超過這個值才重建牌陣，避免拖曳視窗時每個像素都重建（解碼圖片很貴）。</summary>
    public const double RebuildThreshold = 1;

    /// <summary>
    /// 依可用的寬高算出最大的牌寬（夾在 <see cref="MinCardWidth"/>～<see cref="MaxCardWidth"/>）。
    /// 牌陣用資料原座標（<see cref="TarotArrangement.Original"/>）、有編號圓點，所以外框要算進 <see cref="TarotStageLayout.NumberedGutter"/> 的留白。
    /// 做法：先以牌寬 1 算出整個畫布的外框，扣掉留白就是「每單位牌寬」的外框，再除。
    /// </summary>
    public static double CardWidthFor(TarotSpreadModel spread, double availableWidth, double availableHeight)
    {
        if (spread.Cards.Count == 0)
            return DefaultCardWidth;

        var unit = TarotStageLayout.Compute(spread.Cards, 1, TarotArrangement.Original, showNumbers: true, showCaptions: false);
        var gutter = 2 * TarotStageLayout.NumberedGutter;
        var extentWidth = unit.Width - gutter;
        var extentHeight = unit.Height - gutter;
        if (extentWidth <= 0 || extentHeight <= 0)
            return DefaultCardWidth;

        var byWidth = (availableWidth - gutter) / extentWidth;
        var byHeight = (availableHeight - gutter) / extentHeight;
        return Math.Clamp(Math.Min(byWidth, byHeight), MinCardWidth, MaxCardWidth);
    }

    /// <summary>新的牌寬與目前差超過 <see cref="RebuildThreshold"/> 才需要更新。</summary>
    public static bool ShouldUpdate(double current, double next) => Math.Abs(next - current) > RebuildThreshold;
}
