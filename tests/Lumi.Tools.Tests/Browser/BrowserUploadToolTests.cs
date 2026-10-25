using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Lumi.Tools.Tests.Files;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserUploadToolTests : IDisposable
{
    private readonly FakeBrowserSessions _sessions = new();
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();

    private BrowserUploadTool Tool => new(_sessions);

    private ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System,
        new FileScope([new ScopeRoot("專案", _tree.Root, true)]));

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public void Descriptor_IsWriteWorkbench_RequiresScope_AndAlwaysAsks()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_upload", d.Name);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(120), d.Timeout);
        Assert.True(d.RequiresScope);
        Assert.Equal(ToolGroups.Browser, d.Group);
        Assert.False(d.CanAlwaysAllow);
    }

    [Fact]
    public void Preview_ShowsFileAndHost()
    {
        _tree.Write("資料/報告.pdf");

        var preview = Tool.Preview("""{"ref":"e3","path":"資料/報告.pdf"}""", Context);

        Assert.Equal("上傳 資料/報告.pdf 到 example.com", preview.Detail);
    }

    [Fact]
    public async Task Execute_UploadsTheResolvedFile()
    {
        _tree.Write("資料/報告.pdf");

        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":"資料/報告.pdf"}""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("已上傳檔案", result.Summary);
        var call = Assert.Single(_sessions.Calls);
        Assert.StartsWith("upload e3 ", call);
        Assert.EndsWith("報告.pdf", call);
    }

    [Fact]
    public async Task Execute_RejectsPathOutsideScope()
    {
        var outsideFile = _outside.Write("secret.txt");

        var result = await Tool.ExecuteAsync($$"""{"ref":"e3","path":"{{outsideFile.Replace("\\", "\\\\")}}"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("無法讀取", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_RejectsFolders()
    {
        _tree.Dir("資料");

        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":"資料"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("不是檔案", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_RequiresPath()
    {
        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":" "}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
    }

    [Fact]
    public async Task Execute_MapsBrowserException_ToFailure()
    {
        _tree.Write("a.txt");
        _sessions.Throw = new BrowserException(BrowserFailureKind.StaleRef, "找不到 e3，頁面可能已經變了，請先 browser_snapshot。");

        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":"a.txt"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("找不到元素", result.Summary);
    }
}
