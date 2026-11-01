using Lumi.Core.Tools;

namespace Lumi.Tarot.Tests;

public sealed class TarotCardToolTests
{
    private static TarotCardTool Tool(TarotDeck? deck = null) => new(deck ?? TarotTestSupport.SeededDeck());

    [Fact]
    public async Task Card_MajorZero_ReturnsTheFool()
    {
        var result = await Tool().ExecuteAsync("""{"card_id":"major_00"}""", TarotTestSupport.Context, default);

        Assert.False(result.IsError);
        Assert.Contains("愚者", result.Content);
        Assert.Equal("愚者", result.Summary);
    }

    [Fact]
    public async Task Card_WandsFourteen_Succeeds()
    {
        var result = await Tool().ExecuteAsync("""{"card_id":"wands_14"}""", TarotTestSupport.Context, default);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Card_UnknownId_IsErrorWithFormatHelp()
    {
        var result = await Tool().ExecuteAsync("""{"card_id":"nope"}""", TarotTestSupport.Context, default);

        Assert.True(result.IsError);
        Assert.StartsWith("找不到牌 nope。", result.Content);
        Assert.Contains("major_00", result.Content);
        Assert.Contains("pentacles_", result.Content);
    }

    [Fact]
    public async Task Card_BrokenDeck_IsErrorAndConstructionDoesNotThrow()
    {
        var tool = new TarotCardTool(TarotTestSupport.BrokenDeck());

        var result = await tool.ExecuteAsync("""{"card_id":"major_00"}""", TarotTestSupport.Context, default);

        Assert.True(result.IsError);
        Assert.Equal("塔羅牌資料有誤，暫時無法使用。", result.Content);
    }

    [Fact]
    public void Descriptor_IsReadOnlyWorkbenchInTarotGroup()
    {
        var descriptor = Tool().Descriptor;

        Assert.Equal("tarot_card", descriptor.Name);
        Assert.Equal(ToolPermission.ReadOnly, descriptor.Permission);
        Assert.Equal(ToolModes.Workbench, descriptor.Modes);
        Assert.Equal(ToolGroups.Tarot, descriptor.Group);
    }
}
