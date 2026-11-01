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
