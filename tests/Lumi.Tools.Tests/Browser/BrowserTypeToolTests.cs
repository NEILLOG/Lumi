using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserTypeToolTests
{
    private readonly FakeBrowserSessions _sessions = new();

    private BrowserTypeTool Tool => new(_sessions);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void Descriptor_IsWriteWorkbench_With60SecondTimeout_InBrowserGroup()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_type", d.Name);
        Assert.Equal("輸入文字", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Browser, d.Group);
    }

    [Fact]
    public void Preview_ShowsCountAndSubmit_ButNeverTheText()
    {
        var preview = Tool.Preview("""{"ref":"e3","text":"hello world","submit":true}""", Context);

        Assert.Equal("[e3] · example.com", preview.Target);
        Assert.Equal("輸入 11 個字，並按 Enter 送出", preview.Detail);
        Assert.DoesNotContain("hello", preview.Detail);
    }

    [Fact]
    public async Task Execute_DefaultsToReplaceWithoutSubmit()
    {
        var result = await Tool.ExecuteAsync("""{"ref":"e3","text":"hello"}""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("已輸入文字", result.Summary);
        Assert.Equal(["type e3 hello True False"], _sessions.Calls);
    }

    [Fact]
    public async Task Execute_PassesReplaceFalseAndSubmitTrue()
    {
        await Tool.ExecuteAsync("""{"ref":"e3","text":"x","replace":false,"submit":true}""", Context, default);

        Assert.Equal(["type e3 x False True"], _sessions.Calls);
    }

    [Fact]
    public async Task Execute_RequiresText()
    {
        // text 是必填的 string：缺少時在參數解析就失敗（回參數不正確），不會進到工具本體
        var result = await Tool.ExecuteAsync("""{"ref":"e3"}""", Context, default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_RejectsTextLongerThan10000Chars()
    {
        var text = new string('字', 10_001);

        var result = await Tool.ExecuteAsync($$"""{"ref":"e3","text":"{{text}}"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_MapsPasswordField_ToFailure()
    {
        _sessions.Throw = new BrowserException(BrowserFailureKind.PasswordField, "這是密碼欄位，不能用 browser_type 輸入。請用 browser_wait_for_user 讓使用者自己輸入密碼。");

        var result = await Tool.ExecuteAsync("""{"ref":"e3","text":"secret"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("不能輸入密碼", result.Summary);
    }
}
