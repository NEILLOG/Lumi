using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;

namespace Lumi.Tools.Tests.Files;

public sealed class WriteFileToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly FileToolOptions _options = new();
    private readonly WriteFileTool _tool;

    public WriteFileToolTests() => _tool = new WriteFileTool(_options);

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private ToolContext CtxFor(ChatMode mode = ChatMode.Workbench) => Context(ScopeOf(_tree), mode);

    private Task<ToolResult> Run(object args, ChatMode mode = ChatMode.Workbench) =>
        _tool.ExecuteAsync(Json(args), CtxFor(mode), default);

    private ToolPreview Preview(object args) => _tool.Preview(Json(args), CtxFor());

    // ---- 描述 ----

    [Fact]
    public void Descriptor_IsWriteOnly_WorkbenchOnly_FifteenSeconds_AndNeedsScope()
    {
        var d = _tool.Descriptor;

        Assert.Equal("write_file", d.Name);
        Assert.Equal("寫入檔案", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.False(d.Modes.Allows(ChatMode.Sprite));   // 小精靈模式呼叫會收到模式不符的錯誤（對話迴圈的既有機制）
        Assert.Equal(TimeSpan.FromSeconds(15), d.Timeout);
        Assert.True(d.RequiresScope);
    }

    [Fact]
    public void Description_SaysWhenToUseIt_NoOverwrite_AndForbiddenExtensions()
    {
        var description = _tool.Descriptor.Description;

        Assert.Contains("不會覆蓋", description);
        Assert.Contains("overwrite_file", description);
        Assert.Contains(".docx", description);
        Assert.Contains("明確要求", description);
    }

    // ---- create ----

    [Fact]
    public async Task Create_NewFile_WritesTheText_AndSummarizesTheLines()
    {
        var result = await Run(new { path = "notes/a.md", mode = "create", text = "第一行\n第二行" });

        Assert.False(result.IsError);
        Assert.Equal("第一行\n第二行", File.ReadAllText(_tree.Path_("notes/a.md")));
        Assert.Equal("新增 2 行", result.Summary);
        Assert.Contains("notes/a.md", result.Content);
    }

    [Fact]
    public async Task Create_CreatesMissingFoldersInsideTheScope()
    {
        var result = await Run(new { path = "a/b/c/new.txt", mode = "create", text = "x" });

        Assert.False(result.IsError);
        Assert.True(File.Exists(_tree.Path_("a/b/c/new.txt")));
    }

    [Fact]
    public async Task Create_ExistingFile_FailsAndKeepsTheOriginal()
    {
        _tree.Write("a.txt", "原本");

        var result = await Run(new { path = "a.txt", mode = "create", text = "新的" });

        Assert.True(result.IsError);
        Assert.Contains("overwrite_file", result.Content);
        Assert.Equal("原本", File.ReadAllText(_tree.Path_("a.txt")));
    }

    [Fact]
    public async Task Create_NewFile_IsUtf8WithoutBom_AndLf_EvenIfTheTextHasCrlf()
    {
        await Run(new { path = "w.txt", mode = "create", text = "甲\r\n乙" });

        Assert.Equal(Encoding.UTF8.GetBytes("甲\n乙"), File.ReadAllBytes(_tree.Path_("w.txt")));
    }

    [Theory]
    [InlineData("utf-8-bom", new byte[] { 0xEF, 0xBB, 0xBF })]
    [InlineData("utf-16", new byte[] { 0xFF, 0xFE })]
    [InlineData("utf-8", new byte[0])]
    public async Task Create_RequestedEncoding_IsWritten(string encoding, byte[] expectedPrefix)
    {
        await Run(new { path = "e.txt", mode = "create", text = "好", encoding });

        var bytes = File.ReadAllBytes(_tree.Path_("e.txt"));
        Assert.Equal(expectedPrefix, bytes.Take(expectedPrefix.Length));
    }

    [Fact]
    public async Task Create_Utf16_RoundTrips()
    {
        await Run(new { path = "u.txt", mode = "create", text = "甲乙", encoding = "utf-16" });

        Assert.Equal("甲乙", File.ReadAllText(_tree.Path_("u.txt"), Encoding.Unicode));
    }

    [Fact]
    public async Task Create_UnknownEncoding_IsErrorAndNothingIsWritten()
    {
        var result = await Run(new { path = "x.txt", mode = "create", text = "x", encoding = "big5" });

        Assert.True(result.IsError);
        Assert.Contains("utf-8", result.Content);
        Assert.False(File.Exists(_tree.Path_("x.txt")));
    }

    [Fact]
    public async Task Create_EmptyText_CreatesAnEmptyFile()
    {
        var result = await Run(new { path = "empty.txt", mode = "create", text = "" });

        Assert.False(result.IsError);
        Assert.Equal(0, new FileInfo(_tree.Path_("empty.txt")).Length);
    }

    [Fact]
    public async Task Create_UnicodeFileName_Works()
    {
        var result = await Run(new { path = "購物清單 🛒.md", mode = "create", text = "牛奶" });

        Assert.False(result.IsError);
        Assert.Equal("牛奶", File.ReadAllText(_tree.Path_("購物清單 🛒.md")));
    }

    // ---- append ----

    [Fact]
    public async Task Append_MissingFile_CreatesIt()
    {
        var result = await Run(new { path = "log.md", mode = "append", text = "第一則" });

        Assert.False(result.IsError);
        Assert.Equal("第一則", File.ReadAllText(_tree.Path_("log.md")));
    }

    [Fact]
    public async Task Append_KeepsEverythingAndAddsTheText()
    {
        _tree.Write("log.md", "# 紀錄\n- 一\n");

        var result = await Run(new { path = "log.md", mode = "append", text = "- 二\n- 三" });

        Assert.False(result.IsError);
        Assert.Equal("# 紀錄\n- 一\n- 二\n- 三", File.ReadAllText(_tree.Path_("log.md")));
        Assert.Equal("追加 2 行", result.Summary);
        Assert.Contains("檔案現在共 4 行", result.Content);
    }

    [Fact]
    public async Task Append_WithoutTrailingNewline_AddsOne_UsingTheFilesCrlf()
    {
        _tree.Write("win.txt", "一\r\n二");

        await Run(new { path = "win.txt", mode = "append", text = "三\n四" });

        Assert.Equal("一\r\n二\r\n三\r\n四", File.ReadAllText(_tree.Path_("win.txt")));
    }

    [Fact]
    public async Task Append_ToAnEmptyFile_AddsNoSeparator()
    {
        _tree.Write("e.txt", "");

        await Run(new { path = "e.txt", mode = "append", text = "x" });

        Assert.Equal("x", File.ReadAllText(_tree.Path_("e.txt")));
    }

    [Fact]
    public async Task Append_Utf8BomFile_DoesNotAddASecondBom()
    {
        var path = _tree.Path_("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("甲\n")]);

        await Run(new { path = "bom.txt", mode = "append", text = "乙" });

        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("甲\n乙")], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Append_Utf16BigEndianFile_StaysBigEndian_AndIgnoresTheEncodingArgument()
    {
        var path = _tree.Path_("be.txt");
        File.WriteAllBytes(path, [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("甲")]);

        var result = await Run(new { path = "be.txt", mode = "append", text = "乙", encoding = "utf-8" });

        Assert.False(result.IsError);
        Assert.Equal([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("甲\n乙")], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Append_EmptyText_IsErrorAndTheFileIsUnchanged()
    {
        _tree.Write("a.txt", "原本\n");

        var result = await Run(new { path = "a.txt", mode = "append", text = "" });

        Assert.True(result.IsError);
        Assert.Equal("原本\n", File.ReadAllText(_tree.Path_("a.txt")));
    }

    [Fact]
    public async Task Append_InvalidUtf8File_IsErrorAndUntouched()
    {
        var path = _tree.Path_("latin1.txt");
        File.WriteAllBytes(path, [0x63, 0x61, 0x66, 0xE9, 0x0A]);

        var result = await Run(new { path = "latin1.txt", mode = "append", text = "x" });

        Assert.True(result.IsError);
        Assert.Equal([0x63, 0x61, 0x66, 0xE9, 0x0A], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Append_BinaryFile_IsErrorAndUntouched()
    {
        var path = _tree.Path_("data.bin");
        File.WriteAllBytes(path, [0x89, 0x50, 0x00, 0x47]);

        var result = await Run(new { path = "data.bin", mode = "append", text = "x" });

        Assert.True(result.IsError);
        Assert.Equal([0x89, 0x50, 0x00, 0x47], File.ReadAllBytes(path));
    }

    // ---- 參數與限制 ----

    [Theory]
    [InlineData("overwrite")]
    [InlineData("replace")]
    [InlineData("CREATE")]
    [InlineData("")]
    public async Task Mode_OnlyCreateOrAppend_IsAccepted(string mode)
    {
        var result = await Run(new { path = "m.txt", mode, text = "x" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(_tree.Path_("m.txt")));
    }

    [Fact]
    public async Task MissingArguments_AreErrors_NotExceptions()
    {
        Assert.True((await _tool.ExecuteAsync("{}", CtxFor(), default)).IsError);
        Assert.True((await Run(new { mode = "create", text = "x" })).IsError);
        Assert.True((await Run(new { path = "a.txt", text = "x" })).IsError);
        Assert.True((await Run(new { path = "a.txt", mode = "create" })).IsError);
        Assert.False(File.Exists(_tree.Path_("a.txt")));
    }

    [Fact]
    public async Task TooLong_IsErrorAndNothingIsWritten()
    {
        _options.MaxWriteChars = 10;

        var result = await Run(new { path = "big.txt", mode = "create", text = new string('字', 11) });

        Assert.True(result.IsError);
        Assert.Contains("10", result.Content);
        Assert.False(File.Exists(_tree.Path_("big.txt")));
    }

    [Fact]
    public async Task AtTheLimit_IsAccepted()
    {
        _options.MaxWriteChars = 10;

        var result = await Run(new { path = "ok.txt", mode = "create", text = new string('字', 10) });

        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData("a.docx")]
    [InlineData("a.DOCX")]
    [InlineData("report.xlsx")]
    [InlineData("slides.pptx")]
    [InlineData("paper.pdf")]
    [InlineData("old.xls")]
    [InlineData("archive.zip")]
    [InlineData("data.tar.gz")]
    public async Task BlockedExtensions_AreRefused_EvenForAnewFile(string path)
    {
        var result = await Run(new { path, mode = "create", text = "x" });

        Assert.True(result.IsError);
        Assert.Contains("安全限制", result.Content);
        Assert.Empty(Snapshot(_tree.Root));
    }

    [Fact]
    public async Task BlockedExtension_ExistingFile_IsNotAppendedTo()
    {
        var path = _tree.Path_("real.docx");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04]);

        var result = await Run(new { path = "real.docx", mode = "append", text = "x" });

        Assert.True(result.IsError);
        Assert.Equal([0x50, 0x4B, 0x03, 0x04], File.ReadAllBytes(path));
    }

    [SkippableFact]
    public async Task SymlinkToABlockedFile_IsRefused_BecauseTheResolvedNameIsBlocked()
    {
        var target = _tree.Write("real.pdf", "%PDF");
        Skip.IfNot(_tree.TryLinkFile("fake.txt", target), "這個環境不能建立符號連結");

        var result = await Run(new { path = "fake.txt", mode = "append", text = "x" });

        Assert.True(result.IsError);
        Assert.Equal("%PDF", File.ReadAllText(target));
    }

    [Fact]
    public async Task PathIsAFolder_IsError()
    {
        Directory.CreateDirectory(_tree.Path_("sub"));

        var result = await Run(new { path = "sub", mode = "append", text = "x" });

        Assert.True(result.IsError);
        Assert.Contains("資料夾", result.Content);
    }

    [Fact]
    public async Task OutsideTheScope_IsErrorAndNothingIsWrittenOutside()
    {
        var before = Snapshot(_outside.Root);

        var result = await Run(new { path = _outside.Path_("new.txt"), mode = "create", text = "x" });

        Assert.True(result.IsError);
        Assert.Equal(before, Snapshot(_outside.Root));
    }

    // ---- 確認卡片 ----

    [Fact]
    public void Preview_CreateShowsTheRelativePathAndLines()
    {
        var preview = Preview(new { path = "notes/a.md", mode = "create", text = "一\n二" });

        Assert.Equal("notes/a.md", preview.Target);
        Assert.Equal("新增 · 2 行", preview.Detail);
    }

    [Fact]
    public void Preview_AppendToExistingFile_ShowsAddedAndCurrentLines()
    {
        _tree.Write("log.md", "a\nb\nc\n");

        var preview = Preview(new { path = "log.md", mode = "append", text = "d" });

        Assert.Equal("追加 1 行（檔案現有 3 行）", preview.Detail);
    }

    [Fact]
    public void Preview_AppendToMissingFile_IsNewFile()
    {
        var preview = Preview(new { path = "new.md", mode = "append", text = "x\ny" });

        Assert.Equal("新增 · 2 行", preview.Detail);
    }

    [Fact]
    public void Preview_AppendToBigFile_ShowsSizeOnly()
    {
        _tree.Write("big.log", new string('a', 1024 * 1024 + 1024));

        var preview = Preview(new { path = "big.log", mode = "append", text = "x" });

        Assert.Equal("追加 1 行（檔案大小 1 MB）", preview.Detail);
    }

    [Fact]
    public void Preview_AppendToBinaryFile_HasNoDetail()
    {
        var path = _tree.Path_("data.bin");
        File.WriteAllBytes(path, [0x00, 0x01]);

        var preview = Preview(new { path = "data.bin", mode = "append", text = "x" });

        Assert.Equal("data.bin", preview.Target);
        Assert.Null(preview.Detail);
    }

    [Fact]
    public void Preview_OutsideTheScope_ShowsTheGivenString_AndNoDetail()
    {
        var preview = Preview(new { path = "../escape.txt", mode = "create", text = "x" });

        Assert.Equal("../escape.txt", preview.Target);
        Assert.Null(preview.Detail);
    }

    [Fact]
    public void Preview_NeverThrows_OnBadArguments()
    {
        Assert.Equal(ToolPreview.None, _tool.Preview("{不是 json", CtxFor()));
        // 缺必要的引數（text）：反序列化失敗，預覽就是 None，不丟例外
        Assert.Equal(ToolPreview.None, Preview(new { path = "a.txt", mode = "create" }));
        Assert.Null(Preview(new { path = "a\0b", mode = "append", text = "x" }).Detail);
    }
}
