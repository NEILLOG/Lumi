using Lumi.Core.Documents;
using Lumi.Documents.Tests.Samples;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Documents.Tests;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名

/// <summary>
/// 檔案被 Excel、Word 開著時仍要讀得到（FX-11）。Office 開著檔案時持有寫入鎖（只允許別人讀），
/// 所以這裡模擬：用 <c>FileShare.Read</c> 開著、不關。Windows 上舊的 <c>File.OpenRead</c> 會在這裡失敗；
/// macOS 的 <c>FileShare</c> 不一定強制，這些測試在 Mac 上可能本來就會過，需要在 Windows 驗收。
/// </summary>
public class FileInUseTests
{
    [Fact]
    public void Verify_accepts_an_xlsx_that_another_program_holds_open_for_writing()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);
        using var heldByOffice = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        OpenXmlSafety.Verify(path);   // 不丟例外即通過
    }

    [Fact]
    public async Task Xlsx_held_open_for_writing_by_another_program_can_still_be_read()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);
        using var heldByExcel = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        var result = await Reader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(DocumentFormat.Excel, result.Format);
        Assert.Contains("## 專案進度　A1:E5\n", result.Content);
    }

    [Fact]
    public async Task Docx_held_open_for_writing_by_another_program_can_still_be_read()
    {
        using var folder = new TempFolder();
        var path = DocxBuilder.Create(folder.Path);
        using var heldByWord = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        var result = await Reader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(DocumentFormat.Word, result.Format);
        Assert.Contains("蘋果", result.Content);
    }

    private static DocumentReader Reader() => new(
        [new ExcelDocumentReader(), new WordDocumentReader(), new PowerPointDocumentReader(), new PdfDocumentReader()],
        new DocumentOptions(),
        NullLogger<DocumentReader>.Instance);
}
