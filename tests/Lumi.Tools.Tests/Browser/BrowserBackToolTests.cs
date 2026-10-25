using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserBackToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserBackTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsWriteWorkbench_With60SecondTimeout_InBrowserGroup()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_back", d.Name);
        Assert.Equal("回上一頁", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
    }

    [Fact]
    public void Preview_ShowsHost_WhenThereIsAPage()
    {
        Assert.Equal("回上一頁 · example.com", Tool.Preview("{}", Context).Target);
    }

    [Fact]
    public void Preview_ShowsPlainLabel_WhenThereIsNoPage()
    {
        _sessions.Current = null;

        Assert.Equal("回上一頁", Tool.Preview("{}", Context).Target);
    }

    [Fact]
    public async Task Execute_ReturnsWrappedSnapshot()
    {
        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("已回上一頁", result.Summary);
        Assert.Equal(["back"], _sessions.Calls);
        Assert.Contains("<web_content", result.Content);
    }
}
