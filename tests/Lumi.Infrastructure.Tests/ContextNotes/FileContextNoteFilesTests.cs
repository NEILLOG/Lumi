using System.IO.Compression;
using System.Text;
using Lumi.Core.Context;
using Lumi.Core.ContextNotes;
using Lumi.Infrastructure.ContextNotes;

namespace Lumi.Infrastructure.Tests.ContextNotes;

public sealed class FileContextNoteFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-note-files-tests-" + Guid.NewGuid().ToString("N"));

    // 固定時間（UTC，時區也用 UTC），檔名的時間部分才能預期
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 7, 12, 5, TimeSpan.Zero);
    private static readonly string Stamp = "20261116-071205";

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private FileContextNoteFiles NewFiles(TimeProvider? time = null) =>
        new(new ContextNoteFileOptions { Directory = Path.Combine(_directory, "root") }, time ?? new FixedTimeProvider(Now));

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] GifBytes = Encoding.ASCII.GetBytes("GIF89a\u0001\u0000");
    private static readonly byte[] WebpBytes = [.. Encoding.ASCII.GetBytes("RIFF"), 0x10, 0x00, 0x00, 0x00, .. Encoding.ASCII.GetBytes("WEBPVP8 ")];

    [Fact]
    public async Task WriteThenRead_RoundTripsUtf8Text()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        const string text = "# 標題\n\n**目標**：寫報告\n\n## 問題拆解\n- 中文與換行 ✓\n";

        await files.WriteNoteAsync(noteId, text);

        Assert.Equal(text, await files.ReadNoteAsync(noteId));
    }

    [Fact]
    public async Task WriteNote_WritesUtf8WithoutBom()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();

        await files.WriteNoteAsync(noteId, "甲");

        var bytes = await File.ReadAllBytesAsync(Path.Combine(files.GetFolder(noteId), "note.md"));
        Assert.Equal(Encoding.UTF8.GetBytes("甲"), bytes);
    }

    [Fact]
    public async Task WriteNote_Overwrites()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        await files.WriteNoteAsync(noteId, "第一版的內容很長很長很長");

        await files.WriteNoteAsync(noteId, "第二版");

        Assert.Equal("第二版", await files.ReadNoteAsync(noteId));
        Assert.Empty(Directory.GetFiles(files.GetFolder(noteId), ".note-*"));
    }

    [Fact]
    public async Task WriteNote_CreatesMissingFolder()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        Assert.False(Directory.Exists(files.GetFolder(noteId)));

        await files.WriteNoteAsync(noteId, "x");

        Assert.True(Directory.Exists(files.GetFolder(noteId)));
        Assert.True(File.Exists(Path.Combine(files.GetFolder(noteId), "note.md")));
    }

    [Fact]
    public async Task ReadNote_MissingFile_ReturnsNull()
    {
        var files = NewFiles();
        Assert.Null(await files.ReadNoteAsync(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void GetFolder_UsesOptionsDirectoryAndNoteId()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();

        Assert.Equal(Path.Combine(_directory, "root", noteId), files.GetFolder(noteId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("..")]
    public async Task NonGuidNoteId_Throws(string noteId)
    {
        var files = NewFiles();

        Assert.Throws<ArgumentException>(() => files.GetFolder(noteId));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadNoteAsync(noteId));
        await Assert.ThrowsAsync<ArgumentException>(() => files.WriteNoteAsync(noteId, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => files.SaveImageAsync(noteId, PngBytes, "image/png"));
        await Assert.ThrowsAsync<ArgumentException>(() => files.DeleteAsync(noteId));
        Assert.Throws<ArgumentException>(() => files.ResolveImagePath(noteId, "images/a.png"));
    }

    [Fact]
    public async Task SaveImage_AllFourFormats_UseDetectedExtension()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();

        var png = await files.SaveImageAsync(noteId, PngBytes, "image/png");
        var jpg = await files.SaveImageAsync(noteId, JpegBytes, "image/jpeg");
        var gif = await files.SaveImageAsync(noteId, GifBytes, "image/gif");
        var webp = await files.SaveImageAsync(noteId, WebpBytes, "image/webp");

        Assert.Equal($"images/{Stamp}-1.png", png);
        Assert.Equal($"images/{Stamp}-1.jpg", jpg);
        Assert.Equal($"images/{Stamp}-1.gif", gif);
        Assert.Equal($"images/{Stamp}-1.webp", webp);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(files.ResolveImagePath(noteId, png)!));
    }

    [Fact]
    public async Task SaveImage_IgnoresWrongMediaTypeParameter()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();

        // 參數說是 PNG，內容其實是 GIF：副檔名要看內容
        var path = await files.SaveImageAsync(noteId, GifBytes, "image/png");

        Assert.EndsWith(".gif", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveImage_FakeImage_Throws()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        var text = Encoding.UTF8.GetBytes("這不是圖片，只是一段文字");

        await Assert.ThrowsAsync<ArgumentException>(() => files.SaveImageAsync(noteId, text, "image/png"));
        Assert.Empty(Directory.Exists(Path.Combine(files.GetFolder(noteId), "images")) ? Directory.GetFiles(Path.Combine(files.GetFolder(noteId), "images")) : []);
    }

    [Fact]
    public async Task SaveImage_TooBig_Throws()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        var big = new byte[ImageFormats.MaxBytes + 1];
        PngBytes.CopyTo(big, 0);

        await Assert.ThrowsAsync<ArgumentException>(() => files.SaveImageAsync(noteId, big, "image/png"));
    }

    [Fact]
    public async Task SaveImage_AtExactLimit_Succeeds()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        var max = new byte[ImageFormats.MaxBytes];
        PngBytes.CopyTo(max, 0);

        var path = await files.SaveImageAsync(noteId, max, "image/png");

        Assert.EndsWith(".png", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveImage_TwoInSameSecond_GetDifferentNames()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();

        var first = await files.SaveImageAsync(noteId, PngBytes, "image/png");
        var second = await files.SaveImageAsync(noteId, PngBytes, "image/png");

        Assert.Equal($"images/{Stamp}-1.png", first);
        Assert.Equal($"images/{Stamp}-2.png", second);
        Assert.NotNull(files.ResolveImagePath(noteId, first));
        Assert.NotNull(files.ResolveImagePath(noteId, second));
    }

    [Fact]
    public async Task ResolveImagePath_ValidImage_ReturnsFullPath()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        var relative = await files.SaveImageAsync(noteId, PngBytes, "image/png");

        var full = files.ResolveImagePath(noteId, relative);

        Assert.Equal(Path.Combine(files.GetFolder(noteId), "images", Path.GetFileName(relative)), full);
        Assert.True(File.Exists(full));
    }

    [Theory]
    [InlineData("../x.png")]
    [InlineData("images/a/b.png")]
    [InlineData("images/../x.png")]
    [InlineData("images/..\\x.png")]
    [InlineData("images\\a.png")]
    [InlineData("images/")]
    [InlineData("")]
    [InlineData("note.md")]
    [InlineData("images/none.png")]
    public async Task ResolveImagePath_RejectsUnsafeOrMissing(string relative)
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        await files.SaveImageAsync(noteId, PngBytes, "image/png");
        await files.WriteNoteAsync(noteId, "x");
        File.WriteAllText(Path.Combine(files.GetFolder(noteId), "x.png"), "outside");

        Assert.Null(files.ResolveImagePath(noteId, relative));
    }

    [Fact]
    public async Task ResolveImagePath_AbsolutePath_ReturnsNull()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        var saved = await files.SaveImageAsync(noteId, PngBytes, "image/png");
        var absolute = files.ResolveImagePath(noteId, saved)!;

        Assert.Null(files.ResolveImagePath(noteId, absolute));
    }

    [Fact]
    public async Task ExportZip_ContainsNoteAndImages()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        await files.WriteNoteAsync(noteId, "# 研究本\n內容");
        var image = await files.SaveImageAsync(noteId, PngBytes, "image/png");
        var zipPath = Path.Combine(_directory, "export", "out.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        await files.ExportZipAsync(noteId, zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["images/" + Path.GetFileName(image), "note.md"], names);
        using (var reader = new StreamReader(zip.GetEntry("note.md")!.Open(), Encoding.UTF8))
            Assert.Equal("# 研究本\n內容", await reader.ReadToEndAsync());
        using (var stream = zip.GetEntry(image)!.Open())
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory);
            Assert.Equal(PngBytes, memory.ToArray());
        }
    }

    [Fact]
    public async Task ExportZip_ExistingDestination_IsOverwritten()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        await files.WriteNoteAsync(noteId, "新內容");
        var zipPath = Path.Combine(_directory, "out.zip");
        await File.WriteAllTextAsync(zipPath, "舊的不是 zip");

        await files.ExportZipAsync(noteId, zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.NotNull(zip.GetEntry("note.md"));
    }

    [Fact]
    public async Task Delete_RemovesFolder_AndMissingIsFine()
    {
        var files = NewFiles();
        var noteId = Guid.NewGuid().ToString();
        await files.WriteNoteAsync(noteId, "x");
        await files.SaveImageAsync(noteId, PngBytes, "image/png");

        await files.DeleteAsync(noteId);

        Assert.False(Directory.Exists(files.GetFolder(noteId)));
        await files.DeleteAsync(noteId);
        await files.DeleteAsync(Guid.NewGuid().ToString());
    }

    /// <summary>固定時間的 TimeProvider；本地時區用 UTC，檔名時間就是 <see cref="Now"/> 的 UTC 時間。</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
