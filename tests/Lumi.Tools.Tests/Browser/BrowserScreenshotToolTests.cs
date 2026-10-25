using System.Text.Json;
using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserScreenshotToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserScreenshotTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsReadOnlyWorkbench_With60SecondTimeout()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_screenshot", d.Name);
        Assert.Equal("網頁截圖", d.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
    }

    [Fact]
    public async Task DefaultIsViewportOnly()
    {
        await Tool.ExecuteAsync("{}", Context, default);

        Assert.Equal(["screenshot False"], _sessions.Calls);
    }

    [Fact]
    public async Task FullPageTrue_IsPassedThrough()
    {
        await Tool.ExecuteAsync("""{"full_page": true}""", Context, default);

        Assert.Equal(["screenshot True"], _sessions.Calls);
    }

    [Fact]
    public async Task Success_AttachesImage_AndSummarisesSize()
    {
        _sessions.Shot = new BrowserShot([9, 8, 7], "image/jpeg", 1280, 800);

        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("截圖 1280×800", result.Summary);
        Assert.Contains("1280×800", result.Content);
        var image = Assert.Single(result.Images);
        Assert.Equal([9, 8, 7], image.Data);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal("網頁截圖.jpg", image.DisplayName);
    }

    [Fact]
    public async Task OversizedImage_IsRejected_WithoutAttachment()
    {
        _sessions.Shot = new BrowserShot(new byte[BrowserLimits.MaxScreenshotBytes + 1], "image/jpeg", 1280, 800);

        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("截圖太大（超過 5 MB）。", result.Content);
        Assert.Equal("內容太大", result.Summary);
        Assert.Empty(result.Images);
    }

    [Fact]
    public async Task BrowserException_BecomesFailure()
    {
        _sessions.Throw = new BrowserException(BrowserFailureKind.NoPage, "這則對話還沒有開啟的分頁。");

        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("沒有開啟的分頁", result.Summary);
        Assert.Empty(result.Images);
    }
}
