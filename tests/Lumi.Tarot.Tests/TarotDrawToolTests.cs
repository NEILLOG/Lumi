using System.Text.Json;
using Lumi.Core.Tools;

namespace Lumi.Tarot.Tests;

public sealed class TarotDrawToolTests
{
    private static TarotDrawTool Tool(TarotDeck? deck = null) => new(deck ?? TarotTestSupport.SeededDeck());

    [Theory]
    [InlineData("single_card", 1)]
    [InlineData("three_card_timeline", 3)]
    [InlineData("celtic_cross", 10)]
    public async Task Draw_EachSpread_ReturnsParsableCardsWithoutDuplicates(string spread_id, int count)
    {
        var result = await Tool().ExecuteAsync($$"""{"spread_id":"{{spread_id}}"}""", TarotTestSupport.Context, default);

        Assert.False(result.IsError);
        Assert.True(TarotSpreadParser.TryParse(result.Content, out var model));
        Assert.Equal(count, model.Cards.Count);
        Assert.Equal(count, model.Cards.Select(c => c.CardId).Distinct().Count());
    }

    [Fact]
    public async Task Draw_AllowReversedFalse_NoCardIsReversed()
    {
        var result = await Tool().ExecuteAsync("""{"spread_id":"celtic_cross","allow_reversed":false}""", TarotTestSupport.Context, default);

        Assert.False(result.IsError);
        Assert.True(TarotSpreadParser.TryParse(result.Content, out var model));
        Assert.False(model.AllowReversed);
        Assert.All(model.Cards, c => Assert.False(c.IsReversed));
    }

    [Fact]
    public async Task Draw_UnknownSpread_IsErrorListingAvailableSpreads()
    {
        var result = await Tool().ExecuteAsync("""{"spread_id":"nope"}""", TarotTestSupport.Context, default);

        Assert.True(result.IsError);
        Assert.Contains("celtic_cross", result.Content);
    }

    [Fact]
    public async Task Draw_SummaryIsSpreadNameAndCount()
    {
        var result = await Tool().ExecuteAsync("""{"spread_id":"celtic_cross"}""", TarotTestSupport.Context, default);

        Assert.Equal("凱爾特十字・10 張", result.Summary);
    }

    [Fact]
    public async Task Draw_MissingSpreadId_IsArgumentError()
    {
        var result = await Tool().ExecuteAsync("{}", TarotTestSupport.Context, default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
    }

    [Fact]
    public async Task Draw_BrokenDeck_IsErrorAndConstructionDoesNotThrow()
    {
        var deck = TarotTestSupport.BrokenDeck();
        var tool = new TarotDrawTool(deck);

        var result = await tool.ExecuteAsync("""{"spread_id":"single_card"}""", TarotTestSupport.Context, default);

        Assert.True(result.IsError);
        Assert.Equal("塔羅牌資料有誤，暫時無法使用。", result.Content);
    }

    [Fact]
    public void Preview_ShowsSpreadName_AndNoneWhenUnknownOrBroken()
    {
        var tool = Tool();

        Assert.Equal("凱爾特十字", tool.Preview("""{"spread_id":"celtic_cross"}""", TarotTestSupport.Context).Target);
        Assert.Null(tool.Preview("""{"spread_id":"nope"}""", TarotTestSupport.Context).Target);

        var broken = new TarotDrawTool(TarotTestSupport.BrokenDeck());
        Assert.Equal(ToolPreview.None, broken.Preview("""{"spread_id":"single_card"}""", TarotTestSupport.Context));
    }

    [Fact]
    public void Descriptor_IsReadOnlyWorkbenchInTarotGroup()
    {
        var descriptor = Tool().Descriptor;

        Assert.Equal("tarot_draw", descriptor.Name);
        Assert.Equal(ToolPermission.ReadOnly, descriptor.Permission);
        Assert.Equal(ToolModes.Workbench, descriptor.Modes);
        Assert.Equal(ToolGroups.Tarot, descriptor.Group);
    }

    [Fact]
    public void Description_CarriesReadingGuide_AndNoMcpToolNames()
    {
        var description = Tool().Descriptor.Description;

        Assert.Contains("重抽", description);   // 來自 ReadingGuide.Short
        Assert.DoesNotContain("draw_cards", description);
        Assert.DoesNotContain("list_spreads", description);
    }

    [Fact]
    public void InputSchema_SpreadIdIsRequiredAndAllowReversedDefaultsToTrue()
    {
        var schema = Tool().InputSchema.ToJsonString();

        Assert.Contains("spread_id", schema);
        Assert.Contains("allow_reversed", schema);
        Assert.DoesNotContain("seed", schema);
        Assert.DoesNotContain("question", schema);
    }
}
