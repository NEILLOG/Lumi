using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.App.Tests;

/// <summary>
/// 25B：工具卡片的牌陣接線（<see cref="ToolCardViewModel.TarotSpread"/>）。
/// 注意：合法與壞掉的 JSON 兩條，要等 25A 實作 <c>TarotSpreadParser.TryParse</c> 才會通過（Phase 0 會丟 NotImplementedException）。
/// </summary>
public class ToolCardTarotSpreadTests
{
    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    /// <summary>tarot_draw 的結果（與 TarotMcp.Core 的 DrawResultView 同格式，snake_case，三張）。</summary>
    private const string ThreeCardJson = """
        {"spread_id":"three_card_timeline","spread_name":"三張牌","allow_reversed":true,"positions":[
          {"index":0,"position_id":"past","position_name":"過去","position_meaning":"來龍去脈的起點","layout":{"x":-1,"y":0,"rotation":0,"z":0},
           "card":{"id":"major_00","name_zh":"愚者","name_en":"The Fool","orientation":"upright","orientation_zh":"正位","element":"風","keywords":["新開始"],"meaning":"開始","image":"major_00.webp"}},
          {"index":1,"position_id":"present","position_name":"現在","position_meaning":"目前的狀態","layout":{"x":0,"y":0,"rotation":0,"z":0},
           "card":{"id":"swords_03","name_zh":"寶劍三","name_en":"Three of Swords","orientation":"reversed","orientation_zh":"逆位","element":"風","keywords":["心碎"],"meaning":"傷痛","image":"swords_03.webp"}},
          {"index":2,"position_id":"future","position_name":"未來","position_meaning":"可能的走向","layout":{"x":1,"y":0,"rotation":0,"z":0},
           "card":{"id":"cups_01","name_zh":"聖杯一","name_en":"Ace of Cups","orientation":"upright","orientation_zh":"正位","element":"水","keywords":["情感"],"meaning":"開始","image":"cups_01.webp"}}
        ]}
        """;

    private static ToolCardViewModel NewCard(string toolName = "tarot_draw") =>
        new("c1", toolName, "塔羅抽牌", ToolPermission.ReadOnly, "三張牌", "{}",
            new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());

    [Fact]
    public void Complete_with_a_valid_tarot_draw_result_sets_the_spread()
    {
        var card = NewCard();

        card.Complete(new ToolResultBlock("c1", ThreeCardJson, false, "塔羅抽牌", null, "三張牌・3 張", 3, ToolApproval.NotRequired));

        Assert.True(card.HasTarotSpread);
        Assert.Equal(3, card.TarotSpread!.Cards.Count);
    }

    [Fact]
    public void Complete_with_an_error_result_has_no_spread()
    {
        var card = NewCard();

        card.Complete(new ToolResultBlock("c1", "找不到牌陣", true, "塔羅抽牌", null, "失敗", 3, ToolApproval.NotRequired));

        Assert.False(card.HasTarotSpread);
        Assert.Null(card.TarotSpread);
    }

    [Fact]
    public void Complete_for_another_tool_has_no_spread_even_with_valid_json()
    {
        var card = NewCard("echo");

        card.Complete(new ToolResultBlock("c1", ThreeCardJson, false, "回聲", null, "好", 3, ToolApproval.NotRequired));

        Assert.False(card.HasTarotSpread);
    }

    [Fact]
    public void Complete_with_broken_json_has_no_spread_and_does_not_throw()
    {
        var card = NewCard();

        card.Complete(new ToolResultBlock("c1", "{ 不是 json", false, "塔羅抽牌", null, "三張牌", 3, ToolApproval.NotRequired));

        Assert.False(card.HasTarotSpread);
    }
}
