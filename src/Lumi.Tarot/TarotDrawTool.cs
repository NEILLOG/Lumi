using System.ComponentModel;
using Lumi.Core.Tools;
using TarotMcp.Core;

namespace Lumi.Tarot;

public sealed record TarotDrawArgs(
    [property: Description("牌陣 id：single_card（單張，今日指引）、three_card_timeline（三張：過去／現在／未來，看一件事的來龍去脈）、celtic_cross（凱爾特十字 10 張，完整看現況、阻礙與結果）")]
    string SpreadId,
    [property: Description("是否使用逆位；預設 true。使用者說不要逆位時才設 false")]
    bool AllowReversed = true);

/// <summary>為使用者洗牌抽牌（D3）。回傳 <c>DrawResultView</c> 的 JSON，和 MCP server 同格式（D7）。</summary>
public sealed class TarotDrawTool : ToolBase<TarotDrawArgs>
{
    private const string DeckErrorMessage = "塔羅牌資料有誤，暫時無法使用。";

    private static readonly string DescriptionText =
        "為使用者洗牌並抽牌（萊德-韋特-史密斯 78 張牌，繁體中文牌義），依牌陣擺放，回傳每個位置的牌、正逆位、關鍵字與牌義。塔羅是反思工具，僅供娛樂與自我探索。"
        + "\n\n" + ReadingGuide.Short;

    private readonly TarotDeck _deck;

    public TarotDrawTool(TarotDeck deck) => _deck = deck;

    public override ToolDescriptor Descriptor { get; } = new(
        TarotToolNames.Draw, "塔羅抽牌", DescriptionText,
        ToolPermission.ReadOnly, ToolModes.Workbench, TimeSpan.FromSeconds(5),
        Group: ToolGroups.Tarot, Summary: "洗牌、抽牌並依牌陣擺放，回傳每個位置的牌與牌義。");

    protected override ToolPreview Describe(TarotDrawArgs args, ToolContext context)
    {
        try
        {
            var spread = _deck.Deck.FindSpread(args.SpreadId);
            return spread is null ? ToolPreview.None : new ToolPreview(spread.NameZh, null);
        }
        catch (DeckValidationException)
        {
            return ToolPreview.None;
        }
    }

    protected override Task<ToolResult> InvokeAsync(TarotDrawArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            var result = _deck.Draw.Draw(args.SpreadId, args.AllowReversed);
            var json = TarotJson.Serialize(result.ToView());
            return Task.FromResult(ToolResult.Ok(json, $"{result.Spread.NameZh}・{result.Cards.Count} 張"));
        }
        catch (UnknownSpreadException ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
        catch (DeckValidationException)
        {
            return Task.FromResult(ToolResult.Error(DeckErrorMessage));
        }
    }
}
