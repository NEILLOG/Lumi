using System.IO.Compression;
using System.Text;

namespace Lumi.Documents.Tests.Samples;

/// <summary>
/// 損毀、惡意與不符的檔案樣本，全部由程式產生。每個方法回傳檔案內容（bytes），寫檔用 <see cref="SampleFiles.Write"/>。
/// </summary>
public static class BrokenFiles
{
    /// <summary>空檔（0 位元組）。</summary>
    public static byte[] Empty() => [];

    /// <summary>亂碼：固定種子的隨機位元組（檔名仍是 .docx）。</summary>
    public static byte[] Garbage()
    {
        var bytes = new byte[4096];
        new Random(20261023).NextBytes(bytes);
        return bytes;
    }

    /// <summary>截斷的 .docx：只留前半段，zip 目錄不完整。</summary>
    public static byte[] TruncatedDocx()
    {
        var full = DocxBuilder.CreateBytes();
        return full[..(full.Length / 2)];
    }

    /// <summary>
    /// zip bomb：單一項目解壓後 20 MB 的零，壓縮後只有幾十 KB（壓縮比遠超 100）。
    /// 檔名仍是 .docx，項目名稱是 <c>word/document.xml</c>，讓讀取器會去開它。
    /// </summary>
    public static byte[] ZipBomb(int megabytes = 20)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize);
            using var content = entry.Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < megabytes; i++)
                content.Write(chunk, 0, chunk.Length);
        }
        return stream.ToArray();
    }

    /// <summary>項目過多的 zip：<paramref name="entries"/> 個極小的項目（預設超過 10,000 的上限）。</summary>
    public static byte[] TooManyEntries(int entries = 10_001)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < entries; i++)
            {
                var entry = zip.CreateEntry($"part/{i}.xml");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("<a/>");
            }
        }
        return stream.ToArray();
    }

    /// <summary>OLE 複合檔案的檔頭（加密後的 OOXML 就是這個樣子）：檔名仍是 .docx。</summary>
    public static byte[] PasswordProtectedOpenXml()
    {
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>副檔名與內容不符：真正的 PDF 內容，檔名是 .docx。</summary>
    public static byte[] PdfNamedAsDocx() => PdfBuilder.CreateBytes();
}
