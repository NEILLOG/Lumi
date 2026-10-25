using System.Reflection;
using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Lumi.Tools.Browser;
using Lumi.Tools.Tests.Files;
using Xunit;

namespace Lumi.Tools.Tests.Browser;

public sealed class BrowserDownloadToolTests : IDisposable
{
    private readonly FakeBrowserSessions _sessions = new();
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();

    private ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System,
        new FileScope([new ScopeRoot("專案", _tree.Root, true)]));

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private BrowserDownloadTool Tool => new(_sessions);

    [Fact]
    public void Descriptor_IsWriteWorkbench_RequiresScope_With300SecondTimeout()
    {
        var d = Tool.Descriptor;

        Assert.Equal("browser_download", d.Name);
        Assert.Equal("下載檔案", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(300), d.Timeout);
        Assert.True(d.RequiresScope);
        Assert.Equal(ToolGroups.Browser, d.Group);
    }

    [Fact]
    public void Preview_ShowsDestination()
    {
        var preview = Tool.Preview("""{"ref":"e3","path":"下載/報告.pdf"}""", Context);

        Assert.Equal("[e3] · example.com", preview.Target);
        Assert.Equal("下載到 下載/報告.pdf", preview.Detail);
    }

    [Fact]
    public async Task Execute_DownloadsIntoScope_CreatingFolders()
    {
        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":"下載/報告.pdf"}""", Context, default);

        Assert.False(result.IsError);
        Assert.StartsWith("已下載到 下載/報告.pdf", result.Content);
        Assert.Equal(TextFilesSize(_sessions.Download.Bytes), result.Summary);
        Assert.True(Directory.Exists(_tree.Path_("下載")));
        var call = Assert.Single(_sessions.Calls);
        Assert.StartsWith("download e3 ", call);
        Assert.EndsWith($" {WebLimits.DownloadMaxBytes}", call);
    }

    [Theory]
    [InlineData("程式.exe")]
    [InlineData("安裝.MSI")]
    [InlineData("指令.ps1")]
    public async Task Execute_RejectsBlockedExtension_InPath(string name)
    {
        var result = await Tool.ExecuteAsync($$"""{"ref":"e3","path":"{{name}}"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("副檔名不允許", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_NeverOverwritesExistingFile()
    {
        _tree.Write("報告.pdf", "原本的內容");

        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":"報告.pdf"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("檔案已存在", result.Summary);
        Assert.Empty(_sessions.Calls);
        Assert.Equal("原本的內容", File.ReadAllText(_tree.Path_("報告.pdf")));
    }

    [Fact]
    public async Task Execute_RejectsFolderPath()
    {
        _tree.Dir("下載");

        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":"下載"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("是資料夾", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_RejectsPathOutsideScope()
    {
        var outside = _outside.Path_("報告.pdf").Replace("\\", "\\\\");

        var result = await Tool.ExecuteAsync($$"""{"ref":"e3","path":"{{outside}}"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("無法寫入", result.Summary);
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task Execute_DeletesFile_WhenDownloadedNameIsBlocked()
    {
        // 假的工作階段在下載時寫出檔案（真實的瀏覽器會這樣做），然後回報一個被封鎖的檔名
        var proxy = DispatchProxy.Create<IBrowserSessions, WritingSessionsProxy>();
        var handler = (WritingSessionsProxy)(object)proxy;
        handler.Inner = _sessions;
        handler.OnDownload = args => File.WriteAllText((string)args![2]!, "惡意內容");
        _sessions.Download = new BrowserDownload("惡意.exe", 10);

        var result = await new BrowserDownloadTool(proxy).ExecuteAsync("""{"ref":"e3","path":"下載/檔案.pdf"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("副檔名不允許", result.Summary);
        Assert.False(File.Exists(_tree.Path_("下載/檔案.pdf")));
    }

    [Fact]
    public async Task Execute_MapsBrowserException_ToFailure()
    {
        _sessions.Throw = new BrowserException(BrowserFailureKind.Timeout, "等待下載逾時。");

        var result = await Tool.ExecuteAsync("""{"ref":"e3","path":"報告.pdf"}""", Context, default);

        Assert.True(result.IsError);
        Assert.Equal("逾時", result.Summary);
    }

    private static string TextFilesSize(long bytes) => Lumi.Tools.TextFiles.FormatSize(bytes);

    /// <summary>把所有呼叫轉給假的工作階段；下載時先執行 <see cref="OnDownload"/>（寫出檔案）。</summary>
    public class WritingSessionsProxy : DispatchProxy
    {
        public IBrowserSessions? Inner { get; set; }

        public Action<object?[]?>? OnDownload { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IBrowserSessions.DownloadAsync))
                OnDownload?.Invoke(args);
            return targetMethod!.Invoke(Inner, args);
        }
    }
}
