using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserPressToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserPressTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsWriteWorkbench_With30SecondTimeout_InBrowserGroup()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_press", d.Name);
        Assert.Equal("按鍵", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(TimeSpan.FromSeconds(30), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
    }

    [Fact]
    public void Preview_ShowsKeyAndHost()
    {
        var preview = Tool.Preview("""{"key":"enter"}""", Context);

        Assert.Equal("按下 Enter · example.com", preview.Target);
    }

    [Fact]
    public async Task Execute_NormalizesKeyCase()
    {
        var result = await Tool.ExecuteAsync("""{"key":"  arrowdown "}""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal(["press ArrowDown"], _sessions.Calls);
    }

    [Fact]
    public async Task Execute_RejectsKeysOutsideTheList()
    {
        var result = await Tool.ExecuteAsync("""{"key":"F5"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Contains("不支援按鍵「F5」", result.Content);
        Assert.Empty(_sessions.Calls);
    }
}
