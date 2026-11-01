using Lumi.App.Views.Tarot;
using Lumi.Tarot;

namespace Lumi.App.Tests;

/// <summary>25B：牌面的比例尺寸與編號、花色色塊的純函式（不需要 Avalonia）。</summary>
public class TarotCardFaceMetricsTests
{
    [Fact]
    public void Height_is_width_times_12_over_7()
    {
        Assert.Equal(72 * 12.0 / 7, TarotCardFaceMetrics.Height(72), 6);
    }

    [Fact]
    public void Seal_and_plate_stay_inside_the_card()
    {
        var m = TarotCardFaceMetrics.Compute(72);

        Assert.Equal(TarotCardFaceMetrics.Height(72), m.Height, 6);
        Assert.True(m.SealTop >= 0 && m.SealTop + m.SealDiameter <= m.Height);
        Assert.True(m.PlateLeft >= 0 && m.PlateLeft + m.PlateWidth <= m.Width);
        Assert.True(m.PlateTop >= 0 && m.PlateTop + m.PlateHeight <= m.Height);
        // 逆位角標落在牌名板內
        Assert.True(m.BadgeLeft >= m.PlateLeft && m.BadgeLeft + m.BadgeSize <= m.PlateLeft + m.PlateWidth);
        Assert.True(m.BadgeTop >= m.PlateTop && m.BadgeTop + m.BadgeSize <= m.PlateTop + m.PlateHeight);
    }

    [Fact]
    public void Frame_proportions_follow_the_face_width()
    {
        var m = TarotCardFaceMetrics.Compute(100);

        Assert.Equal(2.0, m.FrameStroke, 6);          // 0.02W
        Assert.Equal(6.0, m.FrameCornerRadius, 6);    // 0.06W
        // 圓章與牌名板對齊邊框範本畫好的位置（占牌高）
        Assert.Equal(m.Height * 0.0641, m.PlateHeight, 6);
        Assert.Equal(m.Height * 0.0884, m.SealDiameter, 6);
    }

    [Theory]
    [InlineData("major_00", "0")]
    [InlineData("major_01", "I")]
    [InlineData("major_19", "XIX")]
    [InlineData("major_21", "XXI")]
    [InlineData("wands_05", "5")]
    [InlineData("cups_10", "10")]
    [InlineData("pentacles_01", "1")]
    public void Number_is_roman_for_major_and_arabic_for_pips(string id, string expected) =>
        Assert.Equal(expected, TarotCardFaceMetrics.Number(id));

    [Theory]
    [InlineData("wands_11")]
    [InlineData("cups_14")]
    [InlineData("swords_00")]
    [InlineData("major_22")]
    [InlineData("unknown_03")]
    [InlineData("nounderscore")]
    public void Number_is_null_for_court_cards_and_bad_ids(string id) =>
        Assert.Null(TarotCardFaceMetrics.Number(id));

    [Theory]
    [InlineData("swords_03", "LumiTarotSwordsBrush")]
    [InlineData("wands_01", "LumiTarotWandsBrush")]
    [InlineData("cups_12", "LumiTarotCupsBrush")]
    [InlineData("pentacles_07", "LumiTarotPentaclesBrush")]
    [InlineData("major_00", "LumiTarotMajorBrush")]
    public void SuitBrushKey_follows_the_id_prefix(string id, string expected) =>
        Assert.Equal(expected, TarotCardFaceMetrics.SuitBrushKey(id));
}
