using System.ComponentModel;
using Lumi.Core.Tools;
using TarotMcp.Core;

namespace Lumi.Tarot;

public sealed record TarotCardArgs(
    [property: Description("牌的 id：大牌 major_00～major_21；小牌 wands_01～wands_14、cups_、swords_、pentacles_（11～14 依序是侍者、騎士、皇后、國王）")]
    string CardId);

/// <summary>查詢一張牌的完整牌義（D3）。不抽牌。</summary>
public sealed class TarotCardTool : ToolBase<TarotCardArgs>
{
    private const string DeckErrorMessage = "塔羅牌資料有誤，暫時無法使用。";

    private const string IdHelp =
        "牌的 id 格式：大牌 major_00～major_21；小牌 wands_01～wands_14、cups_01～cups_14、swords_01～swords_14、pentacles_01～pentacles_14。";

    private readonly TarotDeck _deck;

    public TarotCardTool(TarotDeck deck) => _deck = deck;

    public override ToolDescriptor Descriptor { get; } = new(
        TarotToolNames.Card, "查詢塔羅牌",
        "查詢一張塔羅牌的完整資料（正位與逆位的牌義與關鍵字、元素）。使用者問某張牌的意思時使用，不需要抽牌。",
        ToolPermission.ReadOnly, ToolModes.Workbench, TimeSpan.FromSeconds(5),
        Group: ToolGroups.Tarot, Summary: "查詢一張塔羅牌的完整牌義（正位與逆位）。");

    protected override Task<ToolResult> InvokeAsync(TarotCardArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            var card = _deck.Deck.FindCard(args.CardId);
            if (card is null)
            {
                return Task.FromResult(ToolResult.Error($"找不到牌 {args.CardId}。{IdHelp}"));
            }

            var json = TarotJson.Serialize(card.ToDetailView());
            return Task.FromResult(ToolResult.Ok(json, card.NameZh));
        }
        catch (DeckValidationException)
        {
            return Task.FromResult(ToolResult.Error(DeckErrorMessage));
        }
    }
}
