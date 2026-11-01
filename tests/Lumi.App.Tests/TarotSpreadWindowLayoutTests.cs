using Lumi.App.Views.Tarot;
using Lumi.Tarot;

namespace Lumi.App.Tests;

/// <summary>25F：凱爾特十字牌陣視窗的牌寬純函式（<see cref="TarotSpreadWindowLayout"/>）。</summary>
public class TarotSpreadWindowLayoutTests
{
    private static TarotPlacedCard Placed(int index, double x, double y, double rotation = 0, int z = 0) =>
        new(index, "位置" + index, "major_0" + index, "牌" + index, "EN" + index, "風", false, x, y, rotation, z);

    /// <summary>凱爾特十字，座標與 Headless 測試相同（plan/25 「牌陣的實際版面」）。</summary>
    private static TarotSpreadModel CelticCross() => new("凱爾特十字", true,
    [
        Placed(0, 0, 0),
        Placed(1, 0, 0, rotation: 90, z: 1),
        Placed(2, 0, -1),
        Placed(3, 0, 1),
        Placed(4, 1, 0),
        Placed(5, -1, 0),
        Placed(6, 3, 1.5),
        Placed(7, 3, 0.5),
        Placed(8, 3, -0.5),
        Placed(9, 3, -1.5),
    ]) { SpreadId = "celtic_cross" };

    /// <summary>牌寬 w 時，畫布（含編號留白）是否在可用寬高之內。</summary>
    private static bool Fits(TarotSpreadModel spread, double w, double availableWidth, double availableHeight)
    {
        var layout = TarotStageLayout.Compute(spread.Cards, w, TarotArrangement.Original, showNumbers: true, showCaptions: false);
        return layout.Width <= availableWidth + 1e-6 && layout.Height <= availableHeight + 1e-6;
    }

    [Fact]
    public void Default_window_gives_about_72()
    {
        // 預設視窗 600 × 760：扣掉標題列上方一列（40）、清單（5 行 × 20 + 4 × 4 = 116 ＋下方 16）、說明列（36）、牌陣區內距（上下各 8）後，
        // 牌陣區高 552，可用高 536；寬 568 不是限制。
        var w = TarotSpreadWindowLayout.CardWidthFor(CelticCross(), 600 - 32, 536);

        Assert.InRange(w, 71.5, 72.5);
        Assert.True(Fits(CelticCross(), w, 568, 536));
        Assert.False(Fits(CelticCross(), w + 0.5, 568, 536));   // 已經是最大值
    }

    [Fact]
    public void Bigger_window_gives_bigger_cards_and_smaller_window_smaller_cards()
    {
        var spread = CelticCross();
        var small = TarotSpreadWindowLayout.CardWidthFor(spread, 400, 500);
        var medium = TarotSpreadWindowLayout.CardWidthFor(spread, 600, 760);
        var big = TarotSpreadWindowLayout.CardWidthFor(spread, 900, 1000);

        Assert.True(small < medium);
        Assert.True(medium < big);
    }

    [Fact]
    public void Width_and_height_both_limit_the_card()
    {
        var spread = CelticCross();
        // 寬很大、高很小：高度決定
        var wide = TarotSpreadWindowLayout.CardWidthFor(spread, 2000, 400);
        Assert.True(Fits(spread, wide, 2000, 400));
        Assert.False(Fits(spread, wide + 0.5, 2000, 400));
        // 高很大、寬很小：寬度決定
        var tall = TarotSpreadWindowLayout.CardWidthFor(spread, 300, 3000);
        Assert.True(Fits(spread, tall, 300, 3000));
        Assert.False(Fits(spread, tall + 0.5, 300, 3000));
    }

    [Fact]
    public void Card_width_is_clamped_to_min_and_max()
    {
        var spread = CelticCross();
        Assert.Equal(TarotSpreadWindowLayout.MaxCardWidth, TarotSpreadWindowLayout.CardWidthFor(spread, 10000, 10000));
        Assert.Equal(TarotSpreadWindowLayout.MinCardWidth, TarotSpreadWindowLayout.CardWidthFor(spread, 50, 50));
        Assert.Equal(40, TarotSpreadWindowLayout.MinCardWidth);
        Assert.Equal(160, TarotSpreadWindowLayout.MaxCardWidth);
    }

    [Fact]
    public void Small_changes_do_not_trigger_a_rebuild()
    {
        Assert.False(TarotSpreadWindowLayout.ShouldUpdate(72, 72.5));
        Assert.False(TarotSpreadWindowLayout.ShouldUpdate(72, 71.2));
        Assert.True(TarotSpreadWindowLayout.ShouldUpdate(72, 73.5));
        Assert.True(TarotSpreadWindowLayout.ShouldUpdate(72, 70.9));
    }

    [Fact]
    public void Empty_spread_keeps_the_default_width()
    {
        var empty = new TarotSpreadModel("空", true, []) { SpreadId = "celtic_cross" };
        Assert.Equal(TarotSpreadWindowLayout.DefaultCardWidth, TarotSpreadWindowLayout.CardWidthFor(empty, 500, 500));
    }
}
