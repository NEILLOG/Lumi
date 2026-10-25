using System.Text.Json;
using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserSnapshotToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserSnapshotTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsReadOnlyWorkbench_With60SecondTimeout()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_snapshot", d.Name);
        Assert.Equal("查看網頁", d.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
        Assert.Equal("查看目前網頁。", d.Summary);
    }

    [Fact]
    public void Schema_HasMaxChars()
    {
        Assert.True(Tool.InputSchema["properties"]!.AsObject().ContainsKey("max_chars"));
    }

    [Fact]
    public async Task DefaultMaxChars_IsPassedAs12000()
    {
        await Tool.ExecuteAsync("{}", Context, default);

        Assert.Equal(["snapshot 12000"], _sessions.Calls);
    }

    [Theory]
    [InlineData(100, 2000)]
    [InlineData(999999, 30000)]
    [InlineData(5000, 5000)]
    public async Task MaxChars_IsClampedToRange(int requested, int expected)
    {
        await Tool.ExecuteAsync(JsonSerializer.Serialize(new { max_chars = requested }), Context, default);

        Assert.Equal([$"snapshot {expected}"], _sessions.Calls);
    }

    [Fact]
    public async Task Success_ReturnsWrappedSnapshot_WithElementCountSummary()
    {
        _sessions.Snapshot = new BrowserSnapshot(new Uri("https://example.com/page"), "範例", "- link \"首頁\" [ref=e1]\n- button \"送出\" [ref=e2]", 2, false);

        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.False(result.IsError);
        Assert.Contains("<web_content", result.Content);
        Assert.Contains("共 2 個可操作元素", result.Content);
        Assert.Contains("[ref=e2]", result.Content);
        Assert.Equal("2 個元素", result.Summary);
    }

    [Fact]
    public async Task Truncated_AddsTruncationHint()
    {
        _sessions.Snapshot = new BrowserSnapshot(new Uri("https://example.com/"), null, "- text", 0, true);

        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.Contains("快照太長，已截斷", result.Content);
    }

    [Fact]
    public async Task BrowserException_BecomesFailure()
    {
        _sessions.Throw = new BrowserException(BrowserFailureKind.Closed, "瀏覽器已關閉（閒置太久），請重新用 browser_open 開啟。");

        var result = await Tool.ExecuteAsync("{}", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("瀏覽器已關閉", result.Summary);
        Assert.Contains("browser_open", result.Content);
    }
}
