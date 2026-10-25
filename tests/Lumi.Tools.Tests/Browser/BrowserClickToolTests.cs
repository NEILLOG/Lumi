using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserClickToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserClickTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsWriteWorkbench_With60SecondTimeout_InBrowserGroup()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_click", d.Name);
        Assert.Equal("點擊", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
        Assert.Equal("點擊網頁上的按鈕或連結。", d.Summary);
    }

    [Fact]
    public void Preview_ShowsLabelAndHost_WhenRefIsKnown()
    {
        _sessions.Labels["e3"] = "button \"送出\"";

        var preview = Tool.Preview("""{"ref":"e3"}""", Context);

        Assert.Equal("button \"送出\" · example.com", preview.Target);
    }

    [Fact]
    public void Preview_ShowsRefOnly_WhenLabelIsUnknown()
    {
        var preview = Tool.Preview("""{"ref":"e3"}""", Context);

        Assert.Equal("[e3] · example.com", preview.Target);
    }

    [Fact]
    public void Preview_IsEmpty_WhenRefIsInvalid()
    {
        var preview = Tool.Preview("""{"ref":"12"}""", Context);

        Assert.Null(preview.Target);
    }

    [Fact]
    public async Task Execute_NormalizesBracketedRef_AndReturnsWrappedSnapshot()
    {
        var result = await Tool.ExecuteAsync("""{"ref":"[ref=e3]"}""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("已點擊", result.Summary);
        Assert.Equal(["click e3"], _sessions.Calls);
        Assert.Contains("<web_content", result.Content);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("e")]
    [InlineData("")]
    public async Task Execute_RejectsBadRef_WithoutCallingBrowser(string ref_)
    {
        var result = await Tool.ExecuteAsync($$"""{"ref":"{{ref_}}"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_MapsBrowserException_ToFailure()
    {
        _sessions.Throw = new BrowserException(BrowserFailureKind.StaleRef, "找不到 e3，頁面可能已經變了，請先 browser_snapshot。");

        var result = await Tool.ExecuteAsync("""{"ref":"e3"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("找不到元素", result.Summary);
        Assert.Equal("找不到 e3，頁面可能已經變了，請先 browser_snapshot。", result.Content);
    }
}
