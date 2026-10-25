using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserSelectToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserSelectTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsWriteWorkbench_With60SecondTimeout_InBrowserGroup()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_select", d.Name);
        Assert.Equal("選擇選項", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
    }

    [Fact]
    public void Preview_JoinsOptions()
    {
        var preview = Tool.Preview("""{"ref":"e3","values":["台北","台中"]}""", Context);

        Assert.Equal("選擇 台北、台中", preview.Detail);
    }

    [Fact]
    public async Task Execute_PassesAllValues()
    {
        var result = await Tool.ExecuteAsync("""{"ref":"e3","values":["a","b"]}""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("已選擇", result.Summary);
        Assert.Equal(["select e3 a|b"], _sessions.Calls);
    }

    [Fact]
    public async Task Execute_RequiresAtLeastOneValue()
    {
        var result = await Tool.ExecuteAsync("""{"ref":"e3","values":[]}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Empty(_sessions.Calls);
    }
}
