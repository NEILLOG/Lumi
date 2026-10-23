#pragma warning disable CA1416 // 這個檔案的 Unix 權限測試在 Windows 上由 Skip 略過
using System.Text;
using Xunit;

namespace Lumi.Tools.Tests.Files;

/// <summary>寫入的共用基礎：編碼與換行的偵測與沿用、追加補換行、覆寫的原子性、副檔名名單。</summary>
public sealed class TextFileWriterTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static readonly byte[] Bom8 = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Bom16Le = [0xFF, 0xFE];
    private static readonly byte[] Bom16Be = [0xFE, 0xFF];

    // ---- 行數與換行 ----

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\n\n", 2)]
    [InlineData("a\r\nb", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("a\rb", 2)]
    public void CountLines_MatchesReadFile(string text, int expected) =>
        Assert.Equal(expected, TextFileWriter.CountLines(text));

    [Fact]
    public void Normalize_ConvertsEveryNewlineToTheRequestedOne()
    {
        Assert.Equal("a\nb\nc", TextFileWriter.Normalize("a\r\nb\rc", "\n"));
        Assert.Equal("a\r\nb\r\nc", TextFileWriter.Normalize("a\r\nb\rc", "\r\n"));
    }

    [Theory]
    [InlineData(null, "Utf8", true)]
    [InlineData("", "Utf8", true)]
    [InlineData("utf-8", "Utf8", true)]
    [InlineData("UTF-8-BOM", "Utf8Bom", true)]
    [InlineData(" utf-16 ", "Utf16Le", true)]
    [InlineData("big5", "Utf8", false)]
    [InlineData("shift_jis", "Utf8", false)]
    public void TryParseEncoding_AcceptsOnlyTheThreeWriteEncodings(string? name, string expectedKind, bool ok)
    {
        Assert.Equal(ok, TextFileWriter.TryParseEncoding(name, out var parsed));
        if (ok)
            Assert.Equal(expectedKind, parsed.ToString());
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".DOCX")]
    [InlineData(".Xlsx")]
    [InlineData(".xls")]
    [InlineData(".PDF")]
    [InlineData(".pptx")]
    [InlineData(".ppt")]
    [InlineData(".zip")]
    [InlineData(".7z")]
    [InlineData(".rar")]
    [InlineData(".tar")]
    [InlineData(".GZ")]
    public void BlockedExtensions_AreRefused_CaseInsensitively(string extension)
    {
        Assert.True(TextFileWriter.IsBlocked("a" + extension, out _));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    [InlineData("build.sh")]
    [InlineData("noext")]
    [InlineData("a.docx.txt")]
    public void OrdinaryExtensions_AreNotBlocked(string name) =>
        Assert.False(TextFileWriter.IsBlocked(name, out _));

    // ---- 偵測既有檔案 ----

    [Fact]
    public void Inspect_Utf8WithCrlf_DetectsEncodingNewlineAndLines()
    {
        var path = _tree.Write("a.txt", "第一行\r\n第二行\r\n第三行");

        var info = TextFileWriter.Inspect(path);

        Assert.Equal(TextEncodingKind.Utf8, info.Kind);
        Assert.Equal("\r\n", info.Newline);
        Assert.Equal(3, info.Lines);
        Assert.False(info.EndsWithNewline);
    }

    [Fact]
    public void Inspect_NewlineFollowsTheMajority()
    {
        var mostlyLf = _tree.Write("lf.txt", "a\nb\nc\r\nd\n");
        var mostlyCrlf = _tree.Write("crlf.txt", "a\r\nb\r\nc\nd\r\n");

        Assert.Equal("\n", TextFileWriter.Inspect(mostlyLf).Newline);
        Assert.Equal("\r\n", TextFileWriter.Inspect(mostlyCrlf).Newline);
    }

    [Fact]
    public void Inspect_NoNewlinesAtAll_DefaultsToLf()
    {
        var info = TextFileWriter.Inspect(_tree.Write("one.txt", "只有一行"));

        Assert.Equal("\n", info.Newline);
        Assert.Equal(1, info.Lines);
    }

    [Fact]
    public void Inspect_Utf8Bom_IsDetected()
    {
        var path = _tree.Path_("bom.txt");
        File.WriteAllBytes(path, [.. Bom8, .. Encoding.UTF8.GetBytes("x\ny\n")]);

        var info = TextFileWriter.Inspect(path);

        Assert.Equal(TextEncodingKind.Utf8Bom, info.Kind);
        Assert.Equal(2, info.Lines);
        Assert.True(info.EndsWithNewline);
    }

    [Fact]
    public void Inspect_Utf16LittleAndBigEndian_AreDetected()
    {
        var le = _tree.Path_("le.txt");
        File.WriteAllBytes(le, [.. Bom16Le, .. Encoding.Unicode.GetBytes("甲\r\n乙\r\n")]);
        var be = _tree.Path_("be.txt");
        File.WriteAllBytes(be, [.. Bom16Be, .. Encoding.BigEndianUnicode.GetBytes("甲\n乙")]);

        var leInfo = TextFileWriter.Inspect(le);
        var beInfo = TextFileWriter.Inspect(be);

        Assert.Equal(TextEncodingKind.Utf16Le, leInfo.Kind);
        Assert.Equal("\r\n", leInfo.Newline);
        Assert.Equal(2, leInfo.Lines);
        Assert.Equal(TextEncodingKind.Utf16Be, beInfo.Kind);
        Assert.Equal(2, beInfo.Lines);
    }

    [Fact]
    public void Inspect_EmptyFile_HasNoLines()
    {
        var info = TextFileWriter.Inspect(_tree.Write("empty.txt", ""));

        Assert.Equal(0, info.Size);
        Assert.Equal(0, info.Lines);
        Assert.False(info.EndsWithNewline);
    }

    [Fact]
    public void Inspect_BinaryFile_IsRefused()
    {
        var path = _tree.Path_("data.bin");
        File.WriteAllBytes(path, [0x89, 0x50, 0x00, 0x47]);

        Assert.Throws<InvalidDataException>(() => TextFileWriter.Inspect(path));
    }

    [Fact]
    public void Inspect_InvalidUtf8_IsRefused_NotDecodedAsGarbage()
    {
        var path = _tree.Path_("latin1.txt");
        File.WriteAllBytes(path, [0x63, 0x61, 0x66, 0xE9, 0x0A]);   // 「café」的 Latin-1 位元組

        Assert.Throws<DecoderFallbackException>(() => TextFileWriter.Inspect(path));
    }

    // ---- 新檔 ----

    [Fact]
    public void CreateNew_DefaultIsUtf8WithoutBomAndLf()
    {
        var path = _tree.Path_("new.md");

        TextFileWriter.CreateNew(path, "甲\r\n乙", TextEncodingKind.Utf8);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.GetBytes("甲\n乙"), bytes);
    }

    [Fact]
    public void CreateNew_Utf8Bom_WritesTheBom()
    {
        var path = _tree.Path_("bom.txt");

        TextFileWriter.CreateNew(path, "好", TextEncodingKind.Utf8Bom);

        Assert.Equal([.. Bom8, .. Encoding.UTF8.GetBytes("好")], File.ReadAllBytes(path));
    }

    [Fact]
    public void CreateNew_Utf16_IsLittleEndianWithBom()
    {
        var path = _tree.Path_("u16.txt");

        TextFileWriter.CreateNew(path, "甲\n乙", TextEncodingKind.Utf16Le);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([.. Bom16Le, .. Encoding.Unicode.GetBytes("甲\n乙")], bytes);
    }

    [Fact]
    public void CreateNew_ExistingFile_FailsAndKeepsTheOriginal()
    {
        var path = _tree.Write("a.txt", "原本");

        Assert.Throws<IOException>(() => TextFileWriter.CreateNew(path, "新的", TextEncodingKind.Utf8));

        Assert.Equal("原本", File.ReadAllText(path));
    }

    [Fact]
    public void CreateNew_LeavesNoTemporaryFiles()
    {
        var path = _tree.Path_("only.txt");

        TextFileWriter.CreateNew(path, "內容", TextEncodingKind.Utf8);

        Assert.Equal(["only.txt"], Directory.GetFiles(_tree.Root).Select(Path.GetFileName));
    }

    // ---- 追加 ----

    [Fact]
    public void Append_WithoutTrailingNewline_AddsOneUsingTheFilesNewline()
    {
        var path = _tree.Write("a.txt", "一\r\n二");
        var info = TextFileWriter.Inspect(path);

        TextFileWriter.Append(path, "三\n四", info);

        Assert.Equal("一\r\n二\r\n三\r\n四", File.ReadAllText(path));
    }

    [Fact]
    public void Append_WithTrailingNewline_DoesNotAddAnother()
    {
        var path = _tree.Write("a.txt", "一\n");

        TextFileWriter.Append(path, "二", TextFileWriter.Inspect(path));

        Assert.Equal("一\n二", File.ReadAllText(path));
    }

    [Fact]
    public void Append_EmptyFile_NoSeparator()
    {
        var path = _tree.Write("e.txt", "");

        TextFileWriter.Append(path, "第一行", TextFileWriter.Inspect(path));

        Assert.Equal("第一行", File.ReadAllText(path));
    }

    [Fact]
    public void Append_Utf8Bom_DoesNotWriteASecondBom()
    {
        var path = _tree.Path_("bom.txt");
        File.WriteAllBytes(path, [.. Bom8, .. Encoding.UTF8.GetBytes("甲\n")]);

        TextFileWriter.Append(path, "乙", TextFileWriter.Inspect(path));

        Assert.Equal([.. Bom8, .. Encoding.UTF8.GetBytes("甲\n乙")], File.ReadAllBytes(path));
    }

    [Fact]
    public void Append_Utf16_StaysUtf16()
    {
        var path = _tree.Path_("u16.txt");
        File.WriteAllBytes(path, [.. Bom16Be, .. Encoding.BigEndianUnicode.GetBytes("甲")]);

        TextFileWriter.Append(path, "乙", TextFileWriter.Inspect(path));

        Assert.Equal([.. Bom16Be, .. Encoding.BigEndianUnicode.GetBytes("甲\n乙")], File.ReadAllBytes(path));
    }

    // ---- 覆寫 ----

    [Fact]
    public void Overwrite_KeepsCrlfAndBom()
    {
        var path = _tree.Path_("win.txt");
        File.WriteAllBytes(path, [.. Bom8, .. Encoding.UTF8.GetBytes("舊\r\n舊\r\n")]);

        TextFileWriter.Overwrite(path, "新\n內容", TextFileWriter.Inspect(path));

        Assert.Equal([.. Bom8, .. Encoding.UTF8.GetBytes("新\r\n內容")], File.ReadAllBytes(path));
    }

    [Fact]
    public void Overwrite_KeepsUtf16BigEndian()
    {
        var path = _tree.Path_("be.txt");
        File.WriteAllBytes(path, [.. Bom16Be, .. Encoding.BigEndianUnicode.GetBytes("舊")]);

        TextFileWriter.Overwrite(path, "新", TextFileWriter.Inspect(path));

        Assert.Equal([.. Bom16Be, .. Encoding.BigEndianUnicode.GetBytes("新")], File.ReadAllBytes(path));
    }

    [Fact]
    public void Overwrite_LeavesNoTemporaryFiles()
    {
        var path = _tree.Write("a.txt", "舊");

        TextFileWriter.Overwrite(path, "新", TextFileWriter.Inspect(path));

        Assert.Equal(["a.txt"], Directory.GetFiles(_tree.Root).Select(Path.GetFileName));
        Assert.Equal("新", File.ReadAllText(path));
    }

    [SkippableFact]
    public void Overwrite_WhenTheTemporaryFileCannotBeCreated_LeavesTheOriginalIntact()
    {
        // 寫暫存檔失敗（資料夾不能寫）：原檔內容完好，而且沒有留下暫存檔
        Skip.If(OperatingSystem.IsWindows(), "Windows 的唯讀資料夾行為不同，這裡只驗證 Unix 權限");
        var path = _tree.Write("keep/a.txt", "原本的內容");
        var folder = Path.GetDirectoryName(path)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = Path.Combine(folder, "probe.tmp");
            Skip.If(CanWriteTo(probe), "這個環境（例如 root）不受資料夾權限限制");

            var info = TextFileWriter.Inspect(path);
            var error = Record.Exception(() => TextFileWriter.Overwrite(path, "新的內容", info));
            Assert.True(error is IOException or UnauthorizedAccessException, $"應該因為無法建立暫存檔而失敗，實際是 {error?.GetType().Name}");

            Assert.Equal("原本的內容", File.ReadAllText(path));
            Assert.Equal(["a.txt"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool CanWriteTo(string path)
    {
        try
        {
            File.WriteAllText(path, "x");
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- 大小 ----

    [Fact]
    public void EncodedSize_CountsBytesAndBom()
    {
        Assert.Equal(2, TextFileWriter.EncodedSize("é", TextEncodingKind.Utf8));
        Assert.Equal(5, TextFileWriter.EncodedSize("é", TextEncodingKind.Utf8Bom));
        Assert.Equal(4, TextFileWriter.EncodedSize("甲", TextEncodingKind.Utf16Le));
    }
}
