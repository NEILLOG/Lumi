namespace Lumi.Tarot.Tests;

public sealed class TarotSpreadParserTests
{
    private const string SingleReversedJson = """
        {"spread_id":"single_card","spread_name":"單張牌","allow_reversed":true,"positions":[
          {"index":1,"id":"guidance","position_name":"指引","position_meaning":"x",
           "layout":{"x":0,"y":0,"rotation":0,"z":0},
           "card":{"id":"major_00","name_zh":"愚者","name_en":"The Fool","arcana":"major","suit":null,"number":0,
                   "element":null,"orientation":"reversed","orientation_zh":"逆位","keywords":["a"],"meaning":"m","image":"cards/major_00.webp"}}]}
        """;

    private static async Task<string> DrawJsonAsync(string spread_id)
    {
        var tool = new TarotDrawTool(TarotTestSupport.SeededDeck());
        var result = await tool.ExecuteAsync($$"""{"spread_id":"{{spread_id}}"}""", TarotTestSupport.Context, default);
        Assert.False(result.IsError);
        return result.Content;
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"spread_name":"x","allow_reversed":true}""")]
    [InlineData("""{"spread_name":"x","allow_reversed":true,"positions":[]}""")]
    [InlineData("""{"spread_name":"x","allow_reversed":true,"positions":{}}""")]
    [InlineData("[1,2,3]")]
    public void TryParse_BadInputReturnsFalseWithNullModel(string json)
    {
        var ok = TarotSpreadParser.TryParse(json, out var model);

        Assert.False(ok);
        Assert.Null(model);
    }

    [Fact]
    public void TryParse_MissingRequiredCardField_ReturnsFalse()
    {
        var broken = SingleReversedJson.Replace("\"name_zh\":\"愚者\",", "");

        Assert.False(TarotSpreadParser.TryParse(broken, out var model));
        Assert.Null(model);
    }

    [Fact]
    public void TryParse_WrongTypeForIndex_ReturnsFalse()
    {
        var broken = SingleReversedJson.Replace("\"index\":1", "\"index\":\"1\"");

        Assert.False(TarotSpreadParser.TryParse(broken, out _));
    }

    [Fact]
    public void TryParse_ReversedOrientationAndNullElement()
    {
        Assert.True(TarotSpreadParser.TryParse(SingleReversedJson, out var model));

        var card = Assert.Single(model.Cards);
        Assert.True(card.IsReversed);
        Assert.Null(card.Element);
        Assert.Equal("愚者", card.CardNameZh);
        Assert.Equal("指引", card.PositionName);
        Assert.Equal("單張牌", model.SpreadName);
        Assert.True(model.AllowReversed);
    }

    [Fact]
    public async Task TryParse_CelticCrossFromDraw_HasTenCardsSortedWithCrossingCard()
    {
        var json = await DrawJsonAsync("celtic_cross");

        Assert.True(TarotSpreadParser.TryParse(json, out var model));
        Assert.Equal(10, model.Cards.Count);
        Assert.Equal(Enumerable.Range(1, 10), model.Cards.Select(c => c.Index));

        var crossing = model.Cards.Single(c => c.PositionName == "交叉");
        Assert.Equal(90, crossing.Rotation, 3);
        Assert.Equal(1, crossing.Z);
    }
}
