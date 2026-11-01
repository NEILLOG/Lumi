using Lumi.App.Resources;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 塔羅牌陣的文字格式（plan/25 25D；ui-spec 3.1）。位置名稱行、對照清單行、滑鼠提示都在這裡組字，
/// 圓點、位置名稱、清單與 25F 的牌陣視窗共用，避免各自拼出不一致的字。
/// </summary>
internal static class TarotText
{
    /// <summary>位置名稱行的兩段：前段是位置名稱（SemiBold），後段是「・中文牌名」加逆位（TextMuted）。</summary>
    public static (string Position, string Tail) CaptionParts(TarotPlacedCard card) =>
        (card.PositionName,
            string.Format(Strings.Tarot_CaptionFormat, string.Empty, card.CardNameZh)
            + (card.IsReversed ? Strings.Tarot_ReversedSuffix : string.Empty));

    /// <summary>位置名稱行的完整文字（也是省略號時的滑鼠提示）。</summary>
    public static string Caption(TarotPlacedCard card)
    {
        var (position, tail) = CaptionParts(card);
        return position + tail;
    }

    /// <summary>
    /// 牌的滑鼠提示：「位置：中文名 英文名（正位／逆位）」；有編號（凱爾特十字）時前面加編號，例「3 目標：命運之輪 Wheel of Fortune（正位）」。
    /// </summary>
    public static string Tooltip(TarotPlacedCard card, int? number = null)
    {
        var text = string.Format(
            Strings.Tarot_PositionTooltipFormat,
            card.PositionName,
            $"{card.CardNameZh} {card.CardNameEn}",
            card.IsReversed ? Strings.Tarot_Reversed : Strings.Tarot_Upright);
        return number is { } n ? string.Format(Strings.Tarot_NumberedTooltipFormat, n, text) : text;
    }
}
