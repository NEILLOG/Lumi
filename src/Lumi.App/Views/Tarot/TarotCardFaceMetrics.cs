using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 牌面的比例尺寸（plan/25 D10、D10b、25B 步驟 3）。全部以牌寬 W 為單位換算，不寫死像素；
/// 牌高 = W × 12/7。純函式，給 <see cref="TarotCardFace"/> 與測試用。
/// </summary>
internal static class TarotCardFaceMetrics
{
    // 圓章與牌名板的位置量自 TarotMcp 的邊框範本 assets/frame/frame.png（2026-11-01）：
    // 文字要剛好落在範本畫好的黑色圓章與牌名板裡；沒有範本時，替代的圓章與牌名板也畫在同一個位置，版面一致。
    // 換了邊框範本要重新量這幾個值。

    /// <summary>圓章上緣（占牌高）。</summary>
    private const double SealTopRatio = 0.0133;

    /// <summary>圓章直徑（占牌高）。</summary>
    private const double SealDiameterRatio = 0.0884;

    /// <summary>牌名板內部的上緣（占牌高）。</summary>
    private const double PlateTopRatio = 0.8792;

    /// <summary>牌名板內部的高（占牌高）。只放得下一行，所以只寫中文名，英文名放在滑鼠提示。</summary>
    private const double PlateHeightRatio = 0.0641;

    /// <summary>牌名板內部的左右內縮（占牌寬）。</summary>
    private const double PlateInsetRatio = 0.15;

    /// <summary>簡易金框的線寬（占牌寬，D10b）。</summary>
    private const double FrameStrokeRatio = 0.02;

    /// <summary>簡易金框的圓角（占牌寬，D10b）。</summary>
    private const double FrameCornerRatio = 0.06;

    /// <summary>圓章線寬下限。</summary>
    private const double MinHairline = 1;

    private static readonly string[] RomanMajor =
    [
        "0", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X",
        "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX", "XXI",
    ];

    /// <summary>牌面的所有尺寸（像素，依 faceWidth 換算）。</summary>
    internal sealed record Sizes(
        double Width,
        double Height,
        double FrameStroke,
        double FrameCornerRadius,
        double SealDiameter,
        double SealTop,
        double SealFontSize,
        double SealStroke,
        double PlateLeft,
        double PlateTop,
        double PlateWidth,
        double PlateHeight,
        double NameZhFontSize,
        double BadgeLeft,
        double BadgeTop,
        double BadgeSize,
        double BadgeFontSize);

    /// <summary>牌高 = 牌寬 × 12/7。</summary>
    public static double Height(double faceWidth) => faceWidth * TarotSpreadLayout.CardAspect;

    public static Sizes Compute(double faceWidth)
    {
        var height = Height(faceWidth);
        var plateLeft = faceWidth * PlateInsetRatio;
        var plateWidth = faceWidth - 2 * plateLeft;
        var plateTop = height * PlateTopRatio;
        var plateHeight = height * PlateHeightRatio;
        var sealDiameter = height * SealDiameterRatio;
        // 「逆」角標是牌名板右端的正方形，和牌名板等高
        var badgeSize = plateHeight;
        return new Sizes(
            Width: faceWidth,
            Height: height,
            FrameStroke: faceWidth * FrameStrokeRatio,
            FrameCornerRadius: faceWidth * FrameCornerRatio,
            SealDiameter: sealDiameter,
            SealTop: height * SealTopRatio,
            SealFontSize: sealDiameter * 0.45,
            SealStroke: Math.Max(MinHairline, faceWidth * 0.01),
            PlateLeft: plateLeft,
            PlateTop: plateTop,
            PlateWidth: plateWidth,
            PlateHeight: plateHeight,
            NameZhFontSize: plateHeight * 0.75,
            BadgeLeft: plateLeft + plateWidth - badgeSize,
            BadgeTop: plateTop,
            BadgeSize: badgeSize,
            BadgeFontSize: badgeSize * 0.75);
    }

    /// <summary>
    /// 圓章裡的編號：大牌 <c>major_NN</c> 用羅馬數字（0 寫「0」）；小牌 01～10 用阿拉伯數字；
    /// 侍者、騎士、皇后、國王（11～14）與格式不對的 id 回 null（圓章留空）。
    /// </summary>
    public static string? Number(string cardId)
    {
        var parts = cardId.Split('_', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return null;
        if (parts[0] == "major")
            return n is >= 0 and < 22 ? RomanMajor[n] : null;
        if (!IsSuit(parts[0]))
            return null;
        return n is >= 1 and <= 10 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>替代牌面（D10b）的花色色塊，回傳 <c>Themes/Tokens.axaml</c> 的 token 名稱。</summary>
    public static string SuitBrushKey(string cardId) => cardId.Split('_', 2)[0] switch
    {
        "wands" => "LumiTarotWandsBrush",
        "cups" => "LumiTarotCupsBrush",
        "swords" => "LumiTarotSwordsBrush",
        "pentacles" => "LumiTarotPentaclesBrush",
        _ => "LumiTarotMajorBrush",
    };

    private static bool IsSuit(string prefix) => prefix is "wands" or "cups" or "swords" or "pentacles";
}
