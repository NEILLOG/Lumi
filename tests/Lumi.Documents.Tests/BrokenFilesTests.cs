using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using Lumi.Documents.Tests.Samples;

namespace Lumi.Documents.Tests;

/// <summary>確認損毀樣本「真的是損毀的」，讀取器的錯誤測試才有意義。</summary>
public class BrokenFilesTests
{
    [Fact]
    public void Empty_file_has_no_bytes()
    {
        Assert.Empty(BrokenFiles.Empty());
    }

    [Fact]
    public void Garbage_is_not_a_zip_container()
    {
        using var stream = new MemoryStream(BrokenFiles.Garbage());

        Assert.ThrowsAny<Exception>(() => new ZipArchive(stream, ZipArchiveMode.Read));
    }

    [Fact]
    public void Truncated_docx_cannot_be_opened()
    {
        using var stream = new MemoryStream(BrokenFiles.TruncatedDocx());

        Assert.ThrowsAny<Exception>(() => WordprocessingDocument.Open(stream, isEditable: false));
    }

    [Fact]
    public void Zip_bomb_is_small_on_disk_but_expands_to_twenty_megabytes()
    {
        var bytes = BrokenFiles.ZipBomb();

        Assert.True(bytes.Length < 1024 * 1024, $"壓縮後應該很小，實際 {bytes.Length} 位元組");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        Assert.NotNull(entry);
        Assert.Equal(20L * 1024 * 1024, entry.Length);
    }

    [Fact]
    public void Too_many_entries_default_is_above_the_limit()
    {
        using var zip = new ZipArchive(new MemoryStream(BrokenFiles.TooManyEntries()), ZipArchiveMode.Read);

        Assert.Equal(10_001, zip.Entries.Count);
    }

    [Fact]
    public void Password_protected_openxml_starts_with_the_ole_signature()
    {
        var bytes = BrokenFiles.PasswordProtectedOpenXml();

        Assert.Equal(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, bytes[..8]);
    }

    [Fact]
    public void Pdf_content_named_as_docx_is_not_a_zip_container()
    {
        using var stream = new MemoryStream(BrokenFiles.PdfNamedAsDocx());

        Assert.ThrowsAny<Exception>(() => new ZipArchive(stream, ZipArchiveMode.Read));
    }
}
