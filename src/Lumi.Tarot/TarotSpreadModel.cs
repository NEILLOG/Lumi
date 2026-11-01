namespace Lumi.Tarot;

/// <summary>畫面用的牌陣（由 tarot_draw 的結果 JSON 解析而來）。</summary>
public sealed record TarotSpreadModel(string SpreadName, bool AllowReversed, IReadOnlyList<TarotPlacedCard> Cards)
{
    /// <summary>牌陣 id（<c>single_card</c>、<c>three_card_timeline</c>、<c>celtic_cross</c>…）：附件依它決定牌寬與排法（ui-spec 3.1）。</summary>
    public string SpreadId { get; init; } = "";
}

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
    int Z)
{
    /// <summary>這個位置的意義（<c>position_meaning</c>），點牌放大的說明區用；沒有時是空字串。</summary>
    public string PositionMeaning { get; init; } = "";

    /// <summary>這張牌在目前正逆位的關鍵字（<c>keywords</c>）。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>這張牌在目前正逆位的牌義（<c>meaning</c>）。</summary>
    public string Meaning { get; init; } = "";
}
