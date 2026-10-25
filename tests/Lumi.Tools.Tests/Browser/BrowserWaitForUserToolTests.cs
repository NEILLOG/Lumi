using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserWaitForUserToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserWaitForUserTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsWriteWorkbench_With610SecondTimeout_AndAsksEveryTime()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_wait_for_user", d.Name);
        Assert.Equal("等待使用者操作", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(610), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
        Assert.False(d.CanAlwaysAllow);
    }

    [Fact]
    public void Preview_ShowsMessageAndCondition()
    {
        var preview = Tool.Preview("""{"message":"請在視窗登入","done_when_text":"登出"}""", Context);

        Assert.Equal("等待你在瀏覽器操作 · example.com", preview.Target);
        Assert.Contains("請在視窗登入", preview.Detail);
        Assert.Contains("頁面出現「登出」", preview.Detail);
        Assert.Contains("最多 180 秒", preview.Detail);
    }

    [Fact]
    public async Task Execute_PassesConditionAndDefaultTimeout()
    {
        var result = await Tool.ExecuteAsync("""{"message":"請登入","done_when_text":"登出"}""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("使用者已完成", result.Summary);
        Assert.Contains("使用者已完成操作。", result.Content);
        Assert.Equal(["wait |登出 180"], _sessions.Calls);
    }

    [Fact]
    public async Task Execute_PassesUrlCondition()
    {
        await Tool.ExecuteAsync("""{"message":"請登入","done_when_url_contains":"/home"}""", Context, default);

        Assert.Equal(["wait /home| 180"], _sessions.Calls);
    }

    [Fact]
    public async Task Execute_ClampsTimeoutToRange()
    {
        await Tool.ExecuteAsync("""{"message":"請登入","done_when_text":"登出","timeout_seconds":5}""", Context, default);
        await Tool.ExecuteAsync("""{"message":"請登入","done_when_text":"登出","timeout_seconds":9999}""", Context, default);

        Assert.Equal(["wait |登出 10", "wait |登出 600"], _sessions.Calls);
    }

    [Fact]
    public async Task Execute_RequiresACompletionCondition()
    {
        var result = await Tool.ExecuteAsync("""{"message":"請登入"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Contains("done_when_url_contains", result.Content);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_RequiresMessage()
    {
        var result = await Tool.ExecuteAsync("""{"message":"  ","done_when_text":"登出"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("缺少 message（要告訴使用者的話）。", result.Content);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_ReportsTimeout_AsNormalResult()
    {
        _sessions.Wait = new BrowserWaitResult(false, "等待逾時，使用者還沒有完成操作。", _sessions.Snapshot);

        var result = await Tool.ExecuteAsync("""{"message":"請登入","done_when_text":"登出"}""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("等待逾時", result.Summary);
        Assert.Contains("等待逾時，使用者還沒有完成操作。", result.Content);
    }
}
