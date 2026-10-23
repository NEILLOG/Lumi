#pragma warning disable CA1416 // 這個檔案的 Unix 權限測試在 Windows 上由 Skip 略過
using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;

namespace Lumi.Tools.Tests.Files;

public sealed class OverwriteFileToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly FileToolOptions _options = new();
    private readonly OverwriteFileTool _tool;

    public OverwriteFileToolTests() => _tool = new OverwriteFileTool(_options);

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private ToolContext CtxFor(ChatMode mode = ChatMode.Workbench) => Context(ScopeOf(_tree), mode);

    private Task<ToolResult> Run(object args, ChatMode mode = ChatMode.Workbench) =>
        _tool.ExecuteAsync(Json(args), CtxFor(mode), default);

    private ToolPreview Preview(object args) => _tool.Preview(Json(args), CtxFor());

    [Fact]
    public void Descriptor_IsDestructive_WorkbenchOnly_FifteenSeconds_AndNeedsScope()
    {
        var d = _tool.Descriptor;

        Assert.Equal("overwrite_file", d.Name);
        Assert.Equal("覆寫檔案", d.DisplayName);
        Assert.Equal(ToolPermission.Destructive, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.False(d.Modes.Allows(ChatMode.Sprite));
        Assert.Equal(TimeSpan.FromSeconds(15), d.Timeout);
        Assert.True(d.RequiresScope);
    }

    [Fact]
    public void Description_WarnsThatContentIsLost_AndPointsToAppend()
    {
        var description = _tool.Descriptor.Description;

        Assert.Contains("丟掉", description);
        Assert.Contains("write_file", description);
        Assert.Contains("append", description);
    }

    [Fact]
    public async Task Replaces_TheWholeContent()
    {
        _tree.Write("a.md", "舊的第一行\n舊的第二行\n");

        var result = await Run(new { path = "a.md", text = "全新的內容" });

        Assert.False(result.IsError);
        Assert.Equal("全新的內容", File.ReadAllText(_tree.Path_("a.md")));
        Assert.Equal("覆寫為 1 行", result.Summary);
        Assert.Contains("2 行 → 1 行", result.Content);
    }

    [Fact]
    public async Task KeepsCrlfNewlines_EvenIfTheTextUsesLf()
    {
        _tree.Write("win.txt", "a\r\nb\r\n");

        await Run(new { path = "win.txt", text = "x\ny\nz" });

        Assert.Equal("x\r\ny\r\nz", File.ReadAllText(_tree.Path_("win.txt")));
    }

    [Fact]
    public async Task KeepsLfNewlines_EvenIfTheTextUsesCrlf()
    {
        _tree.Write("unix.txt", "a\nb\n");

        await Run(new { path = "unix.txt", text = "x\r\ny" });

        Assert.Equal("x\ny", File.ReadAllText(_tree.Path_("unix.txt")));
    }

    [Fact]
    public async Task KeepsTheUtf8Bom()
    {
        var path = _tree.Path_("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("舊")]);

        await Run(new { path = "bom.txt", text = "新" });

        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("新")], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task KeepsUtf16LittleEndian()
    {
        var path = _tree.Path_("le.txt");
        File.WriteAllBytes(path, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("舊\r\n")]);

        await Run(new { path = "le.txt", text = "新\n好" });

        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("新\r\n好")], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task EmptyText_TruncatesTheFile()
    {
        _tree.Write("a.txt", "原本");

        var result = await Run(new { path = "a.txt", text = "" });

        Assert.False(result.IsError);
        Assert.Equal(0, new FileInfo(_tree.Path_("a.txt")).Length);
    }

    [Fact]
    public async Task MissingFile_IsErrorAndNothingIsCreated()
    {
        var result = await Run(new { path = "nope.md", text = "x" });

        Assert.True(result.IsError);
        Assert.Contains("write_file", result.Content);
        Assert.Empty(Snapshot(_tree.Root));
    }

    [Fact]
    public async Task PathIsAFolder_IsError()
    {
        Directory.CreateDirectory(_tree.Path_("sub"));

        var result = await Run(new { path = "sub", text = "x" });

        Assert.True(result.IsError);
        Assert.Contains("資料夾", result.Content);
    }

    [Fact]
    public async Task BinaryFile_IsErrorAndUntouched()
    {
        var path = _tree.Path_("img.bin");
        File.WriteAllBytes(path, [0x89, 0x50, 0x00, 0x47]);

        var result = await Run(new { path = "img.bin", text = "x" });

        Assert.True(result.IsError);
        Assert.Equal([0x89, 0x50, 0x00, 0x47], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task InvalidUtf8_IsErrorAndUntouched()
    {
        var path = _tree.Path_("latin1.txt");
        File.WriteAllBytes(path, [0x63, 0x61, 0x66, 0xE9]);

        var result = await Run(new { path = "latin1.txt", text = "x" });

        Assert.True(result.IsError);
        Assert.Equal([0x63, 0x61, 0x66, 0xE9], File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("old.docx")]
    [InlineData("OLD.XLSX")]
    [InlineData("deck.pptx")]
    public async Task BlockedExtension_IsRefused_EvenWhenItExists(string name)
    {
        var path = _tree.Path_(name);
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04]);

        var result = await Run(new { path = name, text = "純文字" });

        Assert.True(result.IsError);
        Assert.Equal([0x50, 0x4B, 0x03, 0x04], File.ReadAllBytes(path));
    }

    [SkippableFact]
    public async Task Atomic_TempFileCannotBeWritten_OriginalStaysIntactAndNoTempLeftBehind()
    {
        Skip_IfWindows();
        var path = _tree.Write("keep/a.txt", "原本的內容\n");
        var folder = Path.GetDirectoryName(path)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Skip_IfCanWrite(folder);

            var result = await Run(new { path = "keep/a.txt", text = "新的內容" });

            Assert.True(result.IsError);
            Assert.Equal("原本的內容\n", File.ReadAllText(path));
            Assert.Equal(["a.txt"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    public async Task SymlinkToAFileOutsideTheScope_IsRefused_AndTheTargetIsUntouched()
    {
        var target = _outside.Write("secret.txt", "機密");
        Skip.IfNot(_tree.TryLinkFile("link.txt", target), "這個環境不能建立符號連結");

        var result = await Run(new { path = "link.txt", text = "入侵" });

        Assert.True(result.IsError);
        Assert.Equal("機密", File.ReadAllText(target));
    }

    [SkippableFact]
    public async Task SymlinkToAFileInsideTheScope_OverwritesTheRealFile()
    {
        var real = _tree.Write("real/a.txt", "舊");
        Skip.IfNot(_tree.TryLinkFile("alias.txt", real), "這個環境不能建立符號連結");

        var result = await Run(new { path = "alias.txt", text = "新" });

        Assert.False(result.IsError);
        Assert.Equal("新", File.ReadAllText(real));
    }

    [Fact]
    public async Task OutsideTheScope_IsErrorAndTheOutsideFileIsUntouched()
    {
        var outside = _outside.Write("x.md", "別動我");

        var result = await Run(new { path = outside, text = "入侵" });

        Assert.True(result.IsError);
        Assert.Equal("別動我", File.ReadAllText(outside));
    }

    [Fact]
    public async Task UnicodeFileName_Works()
    {
        _tree.Write("購物清單 🛒.md", "舊");

        var result = await Run(new { path = "購物清單 🛒.md", text = "牛奶" });

        Assert.False(result.IsError);
        Assert.Equal("牛奶", File.ReadAllText(_tree.Path_("購物清單 🛒.md")));
    }

    [Fact]
    public async Task TooLong_IsErrorAndUntouched()
    {
        _options.MaxWriteChars = 5;
        _tree.Write("a.txt", "原本");

        var result = await Run(new { path = "a.txt", text = "123456" });

        Assert.True(result.IsError);
        Assert.Equal("原本", File.ReadAllText(_tree.Path_("a.txt")));
    }

    // ---- 確認卡片 ----

    [Fact]
    public void Preview_ShowsBothSidesWithLinesAndSizes()
    {
        _tree.Write("a.md", "a\nb\nc\n");   // 6 位元組、3 行

        var preview = Preview(new { path = "a.md", text = "x" });

        Assert.Equal("a.md", preview.Target);
        Assert.Equal("覆寫：3 行（6 B）→ 1 行（1 B）", preview.Detail);
    }

    [Fact]
    public void Preview_BigOldFile_ShowsOnlyItsSize()
    {
        _tree.Write("big.log", new string('x', 1536 * 1024));   // 1.5 MB

        var preview = Preview(new { path = "big.log", text = "x" });

        Assert.Equal("覆寫：1.5 MB → 1 行（1 B）", preview.Detail);
    }

    [Fact]
    public void Preview_MissingFile_HasNoDetail()
    {
        var preview = Preview(new { path = "none.md", text = "x" });

        Assert.Equal("none.md", preview.Target);
        Assert.Null(preview.Detail);
    }

    [Fact]
    public void Preview_BinaryFile_HasNoDetail_AndDoesNotThrow()
    {
        File.WriteAllBytes(_tree.Path_("b.bin"), [0x00, 0x01]);

        var preview = Preview(new { path = "b.bin", text = "x" });

        Assert.Null(preview.Detail);
    }

    [Fact]
    public void Preview_NeverThrows_OnBadArguments()
    {
        Assert.Equal(ToolPreview.None, _tool.Preview("[1,2", CtxFor()));
        Assert.Null(Preview(new { path = "a\0b", text = "x" }).Detail);
    }

    private static void Skip_IfWindows() => Xunit.Skip.If(OperatingSystem.IsWindows(), "Windows 的唯讀資料夾行為不同，這裡只驗證 Unix 權限");

    private static void Skip_IfCanWrite(string folder)
    {
        var probe = Path.Combine(folder, "probe.tmp");
        bool writable;
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            writable = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            writable = false;
        }
        Xunit.Skip.If(writable, "這個環境（例如 root）不受資料夾權限限制");
    }
}
