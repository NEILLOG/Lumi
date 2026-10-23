using System.IO.Compression;
using System.Xml;
using Lumi.Documents.Tests.Samples;

namespace Lumi.Documents.Tests;

public class OpenXmlSafetyTests
{
    [Theory]
    [InlineData("word")]
    [InlineData("excel")]
    [InlineData("powerpoint")]
    public void Normal_openxml_files_pass(string kind)
    {
        var bytes = kind switch
        {
            "word" => DocxBuilder.CreateBytes(),
            "excel" => XlsxBuilder.CreateBytes(),
            _ => PptxBuilder.CreateBytes(),
        };

        using var stream = new MemoryStream(bytes);
        OpenXmlSafety.Verify(stream);
    }

    [Fact]
    public void Verify_by_path_accepts_a_normal_file()
    {
        using var folder = new TempFolder();
        var path = DocxBuilder.Create(folder.Path);

        OpenXmlSafety.Verify(path);
    }

    [Fact]
    public void Zip_bomb_is_rejected_with_the_fixed_message()
    {
        using var stream = new MemoryStream(BrokenFiles.ZipBomb());

        var ex = Assert.Throws<DocumentRejectedException>(() => OpenXmlSafety.Verify(stream));

        Assert.Equal(OpenXmlSafety.RejectedMessage, ex.Message);
    }

    [Fact]
    public void Default_limit_rejects_more_than_ten_thousand_entries()
    {
        using var stream = new MemoryStream(BrokenFiles.TooManyEntries());

        var ex = Assert.Throws<DocumentRejectedException>(() => OpenXmlSafety.Verify(stream));

        Assert.Equal("這份文件的內部結構異常，已拒絕讀取。", ex.Message);
    }

    [Fact]
    public void Exactly_the_entry_limit_is_allowed()
    {
        using var stream = new MemoryStream(BrokenFiles.TooManyEntries(entries: 10_000));

        OpenXmlSafety.Verify(stream);
    }

    [Fact]
    public void Total_uncompressed_size_over_the_limit_is_rejected()
    {
        var limits = new OpenXmlLimits(MaxTotalBytes: 1000, MaxCompressionRatio: 100, RatioCheckMinBytes: 10L * 1024 * 1024, MaxEntries: 10_000);
        using var stream = new MemoryStream(ZipWith(("a.xml", RandomBytes(600)), ("b.xml", RandomBytes(600))));

        Assert.Throws<DocumentRejectedException>(() => OpenXmlSafety.Verify(stream, limits));
    }

    [Fact]
    public void Total_uncompressed_size_at_the_limit_is_allowed()
    {
        var limits = new OpenXmlLimits(MaxTotalBytes: 1200, MaxCompressionRatio: 100, RatioCheckMinBytes: 10L * 1024 * 1024, MaxEntries: 10_000);
        using var stream = new MemoryStream(ZipWith(("a.xml", RandomBytes(600)), ("b.xml", RandomBytes(600))));

        OpenXmlSafety.Verify(stream, limits);
    }

    [Fact]
    public void High_compression_ratio_over_the_size_threshold_is_rejected()
    {
        var limits = new OpenXmlLimits(MaxTotalBytes: 100L * 1024 * 1024, MaxCompressionRatio: 100, RatioCheckMinBytes: 1000, MaxEntries: 10_000);
        using var stream = new MemoryStream(ZipWith(("a.xml", new byte[100_000])));   // 全零：壓縮比遠超 100

        Assert.Throws<DocumentRejectedException>(() => OpenXmlSafety.Verify(stream, limits));
    }

    [Fact]
    public void High_compression_ratio_below_the_size_threshold_is_allowed()
    {
        // 解壓後不到 RatioCheckMinBytes 的項目不做壓縮比檢查（小檔案壓縮比本來就高）
        var limits = new OpenXmlLimits(MaxTotalBytes: 100L * 1024 * 1024, MaxCompressionRatio: 100, RatioCheckMinBytes: 10_000, MaxEntries: 10_000);
        using var stream = new MemoryStream(ZipWith(("a.xml", new byte[5_000])));

        OpenXmlSafety.Verify(stream, limits);
    }

    [Fact]
    public void Large_incompressible_entry_passes_the_default_rules()
    {
        // 11 MB 隨機資料：超過 10 MB 門檻，但壓縮比接近 1，應該通過
        using var stream = new MemoryStream(ZipWith(("word/media.bin", RandomBytes(11 * 1024 * 1024))));

        OpenXmlSafety.Verify(stream);
    }

    [Fact]
    public void Non_zip_content_throws_invalid_data()
    {
        using var stream = new MemoryStream(BrokenFiles.Garbage());

        Assert.Throws<InvalidDataException>(() => OpenXmlSafety.Verify(stream));
    }

    [Fact]
    public void Verify_leaves_the_stream_open()
    {
        using var stream = new MemoryStream(DocxBuilder.CreateBytes());

        OpenXmlSafety.Verify(stream);

        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Xml_settings_prohibit_dtd_and_limit_characters()
    {
        var settings = OpenXmlSafety.CreateXmlReaderSettings();

        Assert.Equal(DtdProcessing.Prohibit, settings.DtdProcessing);
        Assert.Equal(50L * 1024 * 1024, settings.MaxCharactersInDocument);
    }

    [Fact]
    public void Xml_with_a_doctype_is_rejected_before_entities_expand()
    {
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><x>&e;</x>";
        using var reader = XmlReader.Create(new StringReader(xml), OpenXmlSafety.CreateXmlReaderSettings());

        Assert.Throws<XmlException>(() =>
        {
            while (reader.Read())
            {
            }
        });
    }

    [Fact]
    public void Open_settings_do_not_auto_save()
    {
        var settings = OpenXmlSafety.CreateOpenSettings();

        Assert.False(settings.AutoSave);
        Assert.NotNull(settings.MarkupCompatibilityProcessSettings);
    }

    private static byte[] ZipWith(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entry.Write(content, 0, content.Length);
            }
        }
        return stream.ToArray();
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }
}
