using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.Core.Tests.Context;

public class ToolResultImageTokenTests
{
    private readonly HeuristicTokenEstimator _estimator = new();

    private static ToolResultBlock Result(IReadOnlyList<ImageBlock>? images) =>
        new("t1", "已截圖", false, "截圖", null, null, 0, ToolApproval.NotRequired, images);

    private int Estimate(ToolResultBlock block) =>
        _estimator.EstimateMessages([new ChatMessage("m", ChatRole.Tool, [block], DateTimeOffset.UtcNow)]);

    [Fact]
    public void Each_image_adds_the_same_tokens_as_an_image_block()
    {
        var none = Estimate(Result(null));
        var one = Estimate(Result([new ImageBlock("a.png", "image/png", "a")]));
        var two = Estimate(Result([new ImageBlock("a.png", "image/png", "a"), new ImageBlock("b.png", "image/png", "b")]));

        Assert.Equal(none + _estimator.ImageTokens, one);
        Assert.Equal(none + 2 * _estimator.ImageTokens, two);
    }

    [Fact]
    public void An_empty_image_list_costs_nothing_extra() =>
        Assert.Equal(Estimate(Result(null)), Estimate(Result([])));
}
