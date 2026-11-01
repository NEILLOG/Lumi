using System.Diagnostics.CodeAnalysis;

namespace Lumi.Tarot;

/// <summary>畫面用的牌陣（由 tarot_draw 的結果 JSON 解析而來）。</summary>
public sealed record TarotSpreadModel(string SpreadName, bool AllowReversed, IReadOnlyList<TarotPlacedCard> Cards);

/// <summary>擺在牌陣某個位置的一張牌。X、Y、Rotation、Z 是 spreads.json 的 layout（x 牌寬、y 牌高為單位）。</summary>
public sealed record TarotPlacedCard(
    int Index,
    string PositionName,
    string CardId,
    string CardNameZh,
    string CardNameEn,
    string? Element,
    bool IsReversed,
    double X,
    double Y,
    double Rotation,
    int Z);

public static class TarotSpreadParser
{
    /// <summary>解析 tarot_draw 的結果 JSON；格式不對回 false（不丟例外）。</summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out TarotSpreadModel? model) =>
        throw new NotImplementedException();   // TODO(25A)
}

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
