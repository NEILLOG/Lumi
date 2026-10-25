using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Lumi.Tools.Tests.Files;
using Lumi.Tools.Web;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;

namespace Lumi.Tools.Tests.Web;

public sealed class DownloadFileToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly FakeWebClient _web = new();
    private readonly DownloadFileTool _tool;

    public DownloadFileToolTests() => _tool = new DownloadFileTool(_web);

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private ToolContext CtxFor() => Context(ScopeOf(_tree), ChatMode.Workbench);

    private Task<ToolResult> Run(object args) => _tool.ExecuteAsync(Json(args), CtxFor(), default);

    private ToolPreview Preview(object args) => _tool.Preview(Json(args), CtxFor());

    [Fact]
    public void Descriptor_IsWrite_WorkbenchOnly_ThreeHundredSeconds_NeedsScope_WebGroup()
    {
        var d = _tool.Descriptor;

        Assert.Equal("download_file", d.Name);
        Assert.Equal("下載檔案", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.False(d.Modes.Allows(ChatMode.Sprite));
        Assert.Equal(TimeSpan.FromSeconds(300), d.Timeout);
        Assert.True(d.RequiresScope);
        Assert.Equal(ToolGroups.Web, d.Group);
    }

    // ---- 路徑與檔名 ----

    [Fact]
    public async Task ExplicitFileName_DownloadsIntoScope_WithTheDownloadLimit()
    {
        var result = await Run(new { url = "https://example.com/a/x.pdf", path = "下載/報告.pdf" });

        Assert.False(result.IsError);
        var download = Assert.Single(_web.Downloads);
        Assert.Equal("https://example.com/a/x.pdf", download.Url.AbsoluteUri);
        Assert.EndsWith(Path.Combine("下載", "報告.pdf"), download.Path);
        Assert.Equal(WebLimits.DownloadMaxBytes, download.MaxBytes);
        Assert.Contains("報告.pdf", result.Content);
    }

    [Fact]
    public async Task FolderPath_UsesTheUrlFileName_Decoded()
    {
        var result = await Run(new { url = "https://example.com/reports/Q3%20summary.pdf", path = "下載/" });

        Assert.False(result.IsError);
        Assert.EndsWith(Path.Combine("下載", "Q3 summary.pdf"), Assert.Single(_web.Downloads).Path);
    }

    [Fact]
    public async Task ExistingFolder_WithoutTrailingSlash_AlsoUsesTheUrlFileName()
    {
        _tree.Dir("下載");

        var result = await Run(new { url = "https://example.com/files/data.csv", path = "下載" });

        Assert.False(result.IsError);
        Assert.EndsWith(Path.Combine("下載", "data.csv"), Assert.Single(_web.Downloads).Path);
    }

    [Fact]
    public async Task UrlWithoutFileName_IsError_WhenPathIsAFolder()
    {
        var result = await Run(new { url = "https://example.com/", path = "下載/" });

        Assert.True(result.IsError);
        Assert.Contains("網址沒有檔名", result.Content);
        Assert.Empty(_web.Downloads);
    }

    [Fact]
    public async Task OutsideTheScope_IsError_AndNothingIsDownloaded()
    {
        var absolute = _outside.Path_("x.pdf");

        var relative = await Run(new { url = "https://example.com/x.pdf", path = "../escape.pdf" });
        var outside = await Run(new { url = "https://example.com/x.pdf", path = absolute });

        Assert.True(relative.IsError);
        Assert.True(outside.IsError);
        Assert.Empty(_web.Downloads);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("INSTALL.MSI")]
    [InlineData("run.ps1")]
    [InlineData("image.dmg")]
    [InlineData("tool.Command")]
    public async Task BlockedExtension_IsError_AndNothingIsDownloaded(string name)
    {
        var result = await Run(new { url = "https://example.com/" + name, path = "下載/" + name });

        Assert.True(result.IsError);
        Assert.Equal("副檔名不允許", result.Summary);
        Assert.Empty(_web.Downloads);
    }

    [Fact]
    public async Task ExistingFile_IsNotOverwritten()
    {
        _tree.Write("下載/報告.pdf", "原本");

        var result = await Run(new { url = "https://example.com/x.pdf", path = "下載/報告.pdf" });

        Assert.True(result.IsError);
        Assert.Equal("檔案已存在", result.Summary);
        Assert.Empty(_web.Downloads);
        Assert.Equal("原本", File.ReadAllText(_tree.Path_("下載/報告.pdf")));
    }

    [Fact]
    public async Task MissingParentFolders_AreCreated()
    {
        var result = await Run(new { url = "https://example.com/x.pdf", path = "a/b/c.pdf" });

        Assert.False(result.IsError);
        Assert.True(Directory.Exists(_tree.Path_("a/b")));
        Assert.Single(_web.Downloads);
    }

    [Fact]
    public async Task MissingPath_IsError()
    {
        var result = await Run(new { url = "https://example.com/x.pdf", path = "  " });

        Assert.True(result.IsError);
        Assert.Contains("缺少 path", result.Content);
        Assert.Empty(_web.Downloads);
    }

    [Fact]
    public async Task InvalidUrl_IsError_AndNothingIsDownloaded()
    {
        var result = await Run(new { url = "file:///etc/passwd", path = "下載/x.txt" });

        Assert.True(result.IsError);
        Assert.Empty(_web.Downloads);
    }

    // ---- 結果 ----

    [Fact]
    public async Task Success_ReportsTheSize()
    {
        _web.Download = (url, _, _) => new WebDownloadResult(url, 2048, "application/pdf");

        var result = await Run(new { url = "https://example.com/x.pdf", path = "下載/x.pdf" });

        Assert.False(result.IsError);
        Assert.Contains("已下載到", result.Content);
        Assert.Contains("2 KB", result.Content);
        Assert.Equal("2 KB", result.Summary);
    }

    [Fact]
    public async Task WebRequestException_BecomesAFailure()
    {
        _web.Throw = new WebRequestException(WebFailureKind.TooLarge, "檔案超過 200 MB，已停止下載。");

        var result = await Run(new { url = "https://example.com/huge.zip", path = "下載/huge.zip" });

        Assert.True(result.IsError);
        Assert.Equal("檔案超過 200 MB，已停止下載。", result.Content);
        Assert.Equal("內容太大", result.Summary);
    }

    // ---- 確認卡片 ----

    [Fact]
    public void Preview_ShowsTheTargetAndTheUrl()
    {
        var preview = Preview(new { url = "https://example.com/x.pdf?sig=1", path = "下載/報告.pdf" });

        Assert.NotNull(preview.Url);
        Assert.Contains("報告.pdf", preview.Target);
        Assert.Contains("example.com", preview.Detail);
    }

    [Fact]
    public void Preview_ForFolderPath_ShowsTheFinalFileName()
    {
        var preview = Preview(new { url = "https://example.com/files/report.pdf", path = "下載/" });

        Assert.Contains("report.pdf", preview.Target);
    }

    [Fact]
    public void Preview_WithBadUrl_IsNone()
    {
        var preview = Preview(new { url = "ftp://example.com/x.pdf", path = "下載/x.pdf" });

        Assert.Null(preview.Url);
        Assert.Null(preview.Target);
    }
}
