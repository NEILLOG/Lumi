using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserCloseToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserCloseTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsReadOnlyWorkbench_With30SecondTimeout()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_close", d.Name);
        Assert.Equal("關閉瀏覽器分頁", d.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(30), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
        Assert.Equal("關閉這則對話的瀏覽器分頁。", d.Summary);
    }

    [Fact]
    public async Task Execute_ClosesThisConversationsPage()
    {
        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.False(result.IsError);
        Assert.Equal(["close"], _sessions.Calls);
        Assert.Equal("已關閉瀏覽器分頁。", result.Content);
        Assert.Equal("已關閉", result.Summary);
    }
}
