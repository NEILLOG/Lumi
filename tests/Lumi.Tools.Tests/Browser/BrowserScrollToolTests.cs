using System.Text.Json;
using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserScrollToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserScrollTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsReadOnlyWorkbench_With30SecondTimeout()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_scroll", d.Name);
        Assert.Equal("捲動網頁", d.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(30), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
        Assert.Equal("捲動網頁。", d.Summary);
    }

    [Fact]
    public async Task DefaultDirection_IsDown()
    {
        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.False(result.IsError);
        Assert.Equal(["scroll Down "], _sessions.Calls);
        Assert.Equal("已捲動", result.Summary);
    }

    [Theory]
    [InlineData("up", "Up")]
    [InlineData("TOP", "Top")]
    [InlineData("bottom", "Bottom")]
    public async Task Directions_AreParsedCaseInsensitively(string direction, string expected)
    {
        await Tool.ExecuteAsync(JsonSerializer.Serialize(new { direction }), Context, default);

        Assert.Equal([$"scroll {expected} "], _sessions.Calls);
    }

    [Fact]
    public async Task InvalidDirection_IsParameterError_AndNotCalled()
    {
        var result = await Tool.ExecuteAsync("""{"direction": "left"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("direction 只能是 up、down、top 或 bottom。", result.Content);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Ref_ScrollsToElement_AndAcceptsBrackets()
    {
        await Tool.ExecuteAsync("""{"ref": "[ref=e12]"}""", Context, default);

        Assert.Equal(["scroll Down e12"], _sessions.Calls);
    }

    [Fact]
    public async Task InvalidRef_IsParameterError()
    {
        var result = await Tool.ExecuteAsync("""{"ref": "button"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public void Preview_WithRef_ShowsElementLabelAndHost()
    {
        _sessions.Labels["e3"] = "button \"送出\"";

        var preview = Tool.Preview("""{"ref": "e3"}""", Context);

        Assert.Equal("button \"送出\" · example.com", preview.Target);
    }

    [Fact]
    public void Preview_WithoutRef_IsNone()
    {
        Assert.Equal(ToolPreview.None, Tool.Preview("{}", Context));
    }

    [Fact]
    public async Task BrowserException_BecomesFailure()
    {
        _sessions.Throw = new BrowserException(BrowserFailureKind.StaleRef, "找不到 e12，頁面可能已經變了，請先 browser_snapshot。");

        var result = await Tool.ExecuteAsync("""{"ref": "e12"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("找不到元素", result.Summary);
    }
}
