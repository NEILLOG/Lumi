#pragma warning disable CA1416 // 這個檔案的 Unix 權限測試在 Windows 上由 Skip 略過
using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;

namespace Lumi.Tools.Tests.Files;

/// <summary>copy_file、move_file、create_folder（A5）與它們的確認卡片說明（A6）。</summary>
public sealed class FileOperationToolsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly CopyFileTool _copy = new();
    private readonly MoveFileTool _move = new();
    private readonly CreateFolderTool _folder = new();

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private ToolContext CtxFor(ChatMode mode = ChatMode.Workbench) => Context(ScopeOf(_tree), mode);

    private Task<ToolResult> Copy(object args) => _copy.ExecuteAsync(Json(args), CtxFor(), default);

    private Task<ToolResult> Move(object args) => _move.ExecuteAsync(Json(args), CtxFor(), default);

    private Task<ToolResult> Folder(object args) => _folder.ExecuteAsync(Json(args), CtxFor(), default);

    // ================= 描述 =================

    [Fact]
    public void Descriptors_MatchThePlan()
    {
        Assert.Equal(("copy_file", "複製檔案", ToolPermission.Write, TimeSpan.FromMinutes(5)), Info(_copy.Descriptor));
        Assert.Equal(("move_file", "移動檔案", ToolPermission.Write, TimeSpan.FromMinutes(5)), Info(_move.Descriptor));
        Assert.Equal(("create_folder", "建立資料夾", ToolPermission.Write, TimeSpan.FromSeconds(10)), Info(_folder.Descriptor));

        foreach (var d in new[] { _copy.Descriptor, _move.Descriptor, _folder.Descriptor })
        {
            Assert.Equal(ToolModes.Workbench, d.Modes);
            Assert.False(d.Modes.Allows(ChatMode.Sprite));
            Assert.True(d.RequiresScope);
        }

        static (string, string, ToolPermission, TimeSpan) Info(ToolDescriptor d) => (d.Name, d.DisplayName, d.Permission, d.Timeout);
    }

    // ================= copy_file =================

    [Fact]
    public async Task Copy_CopiesTheBytes_AndKeepsTheSource()
    {
        _tree.Write("src/a.bin", "內容\n第二行");

        var result = await Copy(new { source_path = "src/a.bin", destination_path = "backup/a.bin" });

        Assert.False(result.IsError);
        Assert.Equal("內容\n第二行", File.ReadAllText(_tree.Path_("backup/a.bin")));
        Assert.Equal("內容\n第二行", File.ReadAllText(_tree.Path_("src/a.bin")));
        Assert.Equal("複製 " + SizeOf("內容\n第二行"), result.Summary);
    }

    [Fact]
    public async Task Copy_DestinationExists_FailsAndNothingIsOverwritten()
    {
        _tree.Write("a.txt", "來源");
        _tree.Write("b.txt", "目的原本的內容");

        var result = await Copy(new { source_path = "a.txt", destination_path = "b.txt" });

        Assert.True(result.IsError);
        Assert.Contains("不會覆蓋", result.Content);
        Assert.Equal("目的原本的內容", File.ReadAllText(_tree.Path_("b.txt")));
    }

    [Fact]
    public async Task Copy_DestinationIsAnExistingFolder_Fails()
    {
        _tree.Write("a.txt", "x");
        Directory.CreateDirectory(_tree.Path_("folder"));

        var result = await Copy(new { source_path = "a.txt", destination_path = "folder" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Copy_SourceMissing_IsError()
    {
        var result = await Copy(new { source_path = "none.txt", destination_path = "x.txt" });

        Assert.True(result.IsError);
        Assert.Contains("來源檔案不存在", result.Content);
        Assert.False(File.Exists(_tree.Path_("x.txt")));
    }

    [Fact]
    public async Task Copy_SourceIsAFolder_IsError()
    {
        Directory.CreateDirectory(_tree.Path_("dir"));

        var result = await Copy(new { source_path = "dir", destination_path = "copy" });

        Assert.True(result.IsError);
        Assert.Contains("只能複製檔案", result.Content);
        Assert.False(Directory.Exists(_tree.Path_("copy")));
    }

    [Fact]
    public async Task Copy_SameSourceAndDestination_IsRefused()
    {
        _tree.Write("a.txt", "x");

        var result = await Copy(new { source_path = "a.txt", destination_path = "a.txt" });

        Assert.True(result.IsError);
        Assert.Contains("相同", result.Content);
    }

    [Fact]
    public async Task Copy_CreatesMissingDestinationFolders()
    {
        _tree.Write("a.txt", "x");

        var result = await Copy(new { source_path = "a.txt", destination_path = "x/y/z/a.txt" });

        Assert.False(result.IsError);
        Assert.Equal("x", File.ReadAllText(_tree.Path_("x/y/z/a.txt")));
    }

    [Theory]
    [InlineData("copy.docx")]
    [InlineData("copy.PDF")]
    public async Task Copy_ToABlockedExtension_IsRefused(string destination)
    {
        _tree.Write("a.txt", "x");

        var result = await Copy(new { source_path = "a.txt", destination_path = destination });

        Assert.True(result.IsError);
        Assert.False(File.Exists(_tree.Path_(destination)));
    }

    [Fact]
    public async Task Copy_UnicodeNames_Work()
    {
        _tree.Write("買菜 🛒.md", "牛奶");

        var result = await Copy(new { source_path = "買菜 🛒.md", destination_path = "資料夾/買菜副本.md" });

        Assert.False(result.IsError);
        Assert.Equal("牛奶", File.ReadAllText(_tree.Path_("資料夾/買菜副本.md")));
    }

    [Fact]
    public async Task Copy_SourceOutsideTheScope_IsRefused()
    {
        var outside = _outside.Write("secret.txt", "機密");

        var result = await Copy(new { source_path = outside, destination_path = "stolen.txt" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(_tree.Path_("stolen.txt")));
    }

    [Fact]
    public async Task Copy_SecretSource_IsRefused()
    {
        _tree.Write(".env", "KEY=1");

        var result = await Copy(new { source_path = ".env", destination_path = "copy.txt" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(_tree.Path_("copy.txt")));
    }

    [Fact]
    public async Task Copy_FailureMidway_RemovesThePartialDestination()
    {
        // 取消（相當於逾時或使用者停止）：複製到一半的目的檔要清掉，不留半成品
        _tree.Write("big.bin", new string('x', 3 * 1024 * 1024));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileWriteSupport.CopyNewAsync(_tree.Path_("big.bin"), _tree.Path_("half.bin"), cts.Token));

        Assert.False(File.Exists(_tree.Path_("half.bin")));
        Assert.True(File.Exists(_tree.Path_("big.bin")));
    }

    [Fact]
    public async Task Copy_CopyNewRefusesToTouchAnExistingDestination()
    {
        // 競爭情況：目的在檢查之後才出現。CreateNew 失敗時不能刪掉別人的檔案
        _tree.Write("src.txt", "新");
        _tree.Write("dst.txt", "別人的檔案");

        await Assert.ThrowsAsync<IOException>(() =>
            FileWriteSupport.CopyNewAsync(_tree.Path_("src.txt"), _tree.Path_("dst.txt"), default));

        Assert.Equal("別人的檔案", File.ReadAllText(_tree.Path_("dst.txt")));
    }

    // ================= move_file =================

    [Fact]
    public async Task Move_RenamesInTheSameFolder()
    {
        _tree.Write("old.md", "內容");

        var result = await Move(new { source_path = "old.md", destination_path = "new.md" });

        Assert.False(result.IsError);
        Assert.False(File.Exists(_tree.Path_("old.md")));
        Assert.Equal("內容", File.ReadAllText(_tree.Path_("new.md")));
    }

    [Fact]
    public async Task Move_ToAnotherFolder_CreatesTheFolder()
    {
        _tree.Write("a/x.txt", "x");

        var result = await Move(new { source_path = "a/x.txt", destination_path = "b/c/x.txt" });

        Assert.False(result.IsError);
        Assert.False(File.Exists(_tree.Path_("a/x.txt")));
        Assert.Equal("x", File.ReadAllText(_tree.Path_("b/c/x.txt")));
    }

    [Fact]
    public async Task Move_DestinationExists_FailsAndBothStayAsTheyAre()
    {
        _tree.Write("a.txt", "甲");
        _tree.Write("b.txt", "乙");

        var result = await Move(new { source_path = "a.txt", destination_path = "b.txt" });

        Assert.True(result.IsError);
        Assert.Equal("甲", File.ReadAllText(_tree.Path_("a.txt")));
        Assert.Equal("乙", File.ReadAllText(_tree.Path_("b.txt")));
    }

    [Fact]
    public async Task Move_SourceMissing_IsError()
    {
        var result = await Move(new { source_path = "none.txt", destination_path = "x.txt" });

        Assert.True(result.IsError);
        Assert.Contains("來源檔案不存在", result.Content);
    }

    [Fact]
    public async Task Move_SourceIsAFolder_IsError()
    {
        Directory.CreateDirectory(_tree.Path_("dir"));

        var result = await Move(new { source_path = "dir", destination_path = "moved" });

        Assert.True(result.IsError);
        Assert.Contains("只能移動檔案", result.Content);
        Assert.True(Directory.Exists(_tree.Path_("dir")));
    }

    [Fact]
    public async Task Move_SameSourceAndDestination_IsRefused()
    {
        _tree.Write("a.txt", "x");

        var result = await Move(new { source_path = "a.txt", destination_path = "a.txt" });

        Assert.True(result.IsError);
        Assert.True(File.Exists(_tree.Path_("a.txt")));
    }

    [Fact]
    public async Task Move_CaseOnlyRename_Works_OnEveryFileSystem()
    {
        // 不分大小寫的檔案系統上，目的「存在」其實就是來源本身：要用暫存名兩段式改名
        _tree.Write("readme.md", "內容");

        var result = await Move(new { source_path = "readme.md", destination_path = "README.md" });

        Assert.False(result.IsError);
        Assert.Contains("README.md", Directory.GetFiles(_tree.Root).Select(Path.GetFileName));
        Assert.DoesNotContain("readme.md", Directory.GetFiles(_tree.Root).Select(Path.GetFileName));
        Assert.Equal("內容", File.ReadAllText(_tree.Path_("README.md")));
        Assert.Single(Directory.GetFiles(_tree.Root));   // 沒有殘留的暫存檔
    }

    [Fact]
    public void CaseOnlyRename_IsDetectedOnlyWhereCaseDoesNotMatter()
    {
        var caseInsensitive = FileScopeRules.ForCurrentOs.CaseInsensitive;

        Assert.Equal(caseInsensitive, MoveFileTool.CaseOnlyRename("/x/a.txt", "/x/A.txt"));
        Assert.False(MoveFileTool.CaseOnlyRename("/x/a.txt", "/x/a.txt"));
        Assert.False(MoveFileTool.CaseOnlyRename("/x/a.txt", "/x/b.txt"));
    }

    [Fact]
    public async Task Move_UnicodeNames_Work()
    {
        _tree.Write("購物.md", "牛奶");

        var result = await Move(new { source_path = "購物.md", destination_path = "購物清單 🛒.md" });

        Assert.False(result.IsError);
        Assert.Equal("牛奶", File.ReadAllText(_tree.Path_("購物清單 🛒.md")));
    }

    [Fact]
    public async Task Move_ToABlockedExtension_IsRefused()
    {
        _tree.Write("a.txt", "x");

        var result = await Move(new { source_path = "a.txt", destination_path = "a.xlsx" });

        Assert.True(result.IsError);
        Assert.True(File.Exists(_tree.Path_("a.txt")));
    }

    [Fact]
    public async Task Move_SourceOutsideTheScope_IsRefused_AndDestinationOutsideIsRefused()
    {
        var outside = _outside.Write("o.txt", "外面");
        _tree.Write("in.txt", "裡面");

        var fromOutside = await Move(new { source_path = outside, destination_path = "pulled.txt" });
        var toOutside = await Move(new { source_path = "in.txt", destination_path = _outside.Path_("taken.txt") });

        Assert.True(fromOutside.IsError);
        Assert.True(toOutside.IsError);
        Assert.Equal("外面", File.ReadAllText(outside));
        Assert.True(File.Exists(_tree.Path_("in.txt")));
        Assert.False(File.Exists(_outside.Path_("taken.txt")));
    }

    [Fact]
    public async Task CrossVolumeMove_CopiesVerifiesAndDeletesTheSource()
    {
        // 跨磁碟區的路徑沒有辦法在測試裡造出來，直接驗證它用的那段：複製、比對大小、刪來源
        _tree.Write("src.bin", "跨磁碟的內容");

        await FileWriteSupport.MoveAcrossVolumesAsync(_tree.Path_("src.bin"), _tree.Path_("dst.bin"), default);

        Assert.False(File.Exists(_tree.Path_("src.bin")));
        Assert.Equal("跨磁碟的內容", File.ReadAllText(_tree.Path_("dst.bin")));
    }

    [SkippableFact]
    public async Task CrossVolumeMove_WhenTheSourceCannotBeDeleted_RemovesTheCopy()
    {
        // 來源刪不掉（這裡用「來源是唯讀資料夾」模擬不可控的情況）：目的的複本要清掉，來源保留
        var source = _tree.Write("locked/src.txt", "保留");
        var folder = Path.GetDirectoryName(source)!;
        Skip_IfWindows();
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Skip_IfCanWrite(folder);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                FileWriteSupport.MoveAcrossVolumesAsync(source, _tree.Path_("copy.txt"), default));

            Assert.False(File.Exists(_tree.Path_("copy.txt")));
            Assert.Equal("保留", File.ReadAllText(source));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ================= create_folder =================

    [Fact]
    public async Task CreateFolder_CreatesNestedFolders()
    {
        var result = await Folder(new { path = "a/b/c" });

        Assert.False(result.IsError);
        Assert.True(Directory.Exists(_tree.Path_("a/b/c")));
        Assert.Equal("已建立", result.Summary);
    }

    [Fact]
    public async Task CreateFolder_AlreadyExists_IsNotAnError()
    {
        Directory.CreateDirectory(_tree.Path_("exists"));

        var result = await Folder(new { path = "exists" });

        Assert.False(result.IsError);
        Assert.Contains("已存在", result.Content);
        Assert.Equal("已存在", result.Summary);
    }

    [Fact]
    public async Task CreateFolder_FileWithTheSameName_IsRefused()
    {
        _tree.Write("name", "檔案");

        var result = await Folder(new { path = "name" });

        Assert.True(result.IsError);
        Assert.Contains("同名的檔案", result.Content);
        Assert.Equal("檔案", File.ReadAllText(_tree.Path_("name")));
    }

    [Fact]
    public async Task CreateFolder_UnicodeName_Works()
    {
        var result = await Folder(new { path = "專案 🗂/子資料夾" });

        Assert.False(result.IsError);
        Assert.True(Directory.Exists(_tree.Path_("專案 🗂/子資料夾")));
    }

    [Fact]
    public async Task CreateFolder_OutsideTheScope_IsRefused_AndNothingIsCreatedOutside()
    {
        var name = "esc-" + Guid.NewGuid().ToString("N");
        var outsideFolder = Path.Combine(Path.GetDirectoryName(_tree.Root)!, name);

        var up = await Folder(new { path = "../" + name });
        var sneaky = await Folder(new { path = "a/../../" + name });
        var secret = await Folder(new { path = ".git/hooks" });

        Assert.True(up.IsError);
        Assert.True(sneaky.IsError);
        Assert.True(secret.IsError);
        Assert.False(Directory.Exists(outsideFolder));
        Assert.False(Directory.Exists(_tree.Path_(".git/hooks")));
    }

    // ================= 確認卡片（A6） =================

    [Fact]
    public void CopyPreview_ShowsSourceArrowDestination()
    {
        _tree.Write("a.txt", "x");

        var preview = _copy.Preview(Json(new { source_path = "a.txt", destination_path = "b/c.txt" }), CtxFor());

        Assert.Equal("a.txt", preview.Target);
        Assert.Equal("a.txt → b/c.txt", preview.Detail);
    }

    [Fact]
    public void MovePreview_ShowsSourceArrowDestination()
    {
        var preview = _move.Preview(Json(new { source_path = "a.txt", destination_path = "b.txt" }), CtxFor());

        Assert.Equal("a.txt → b.txt", preview.Detail);
    }

    [Fact]
    public void FolderPreview_SaysCreateFolder()
    {
        var preview = _folder.Preview(Json(new { path = "新資料夾" }), CtxFor());

        Assert.Equal("新資料夾", preview.Target);
        Assert.Equal("建立資料夾", preview.Detail);
    }

    [Fact]
    public void Previews_ShowTheGivenStringWhenOutsideTheScope_AndNeverThrow()
    {
        var copy = _copy.Preview(Json(new { source_path = "../x", destination_path = "/etc/y" }), CtxFor());
        var move = _move.Preview("{broken", CtxFor());
        var folder = _folder.Preview(Json(new { path = "a\0b" }), CtxFor());

        Assert.Equal("../x", copy.Target);
        Assert.Equal("../x → /etc/y", copy.Detail);
        Assert.Equal(ToolPreview.None, move);
        Assert.Equal("a\0b", folder.Target);
    }

    // ---- helpers ----

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

    private static string SizeOf(string text) => TextFiles.FormatSize(Encoding.UTF8.GetByteCount(text));
}
