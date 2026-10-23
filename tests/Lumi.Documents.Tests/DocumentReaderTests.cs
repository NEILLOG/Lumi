using Lumi.Core.Documents;
using Lumi.Documents.Tests.Samples;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Exceptions;

namespace Lumi.Documents.Tests;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名

public class DocumentReaderTests
{
    private static readonly byte[] Pdf = BrokenFiles.PdfNamedAsDocx();

    [Fact]
    public void Supports_only_looks_at_the_extension_and_the_four_formats()
    {
        var reader = StubReaders();

        Assert.True(reader.Supports("a.docx"));
        Assert.True(reader.Supports(@"C:\folder\A.DOCX"));
        Assert.True(reader.Supports("b.xlsx"));
        Assert.True(reader.Supports("b.xlsm"));
        Assert.True(reader.Supports("c.pptx"));
        Assert.True(reader.Supports("d.pdf"));

        Assert.False(reader.Supports("e.doc"));
        Assert.False(reader.Supports("f.xls"));
        Assert.False(reader.Supports("g.ppt"));
        Assert.False(reader.Supports("h.txt"));
        Assert.False(reader.Supports("no-extension"));
        Assert.False(reader.Supports(""));
    }

    [Fact]
    public async Task Unsupported_extension_is_rejected_without_touching_the_file()
    {
        using var folder = new TempFolder();
        var path = folder.Write("notes.txt", "hello"u8.ToArray());

        var result = await StubReaders().ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.UnsupportedMessage);
    }

    [Fact]
    public async Task Legacy_office_formats_get_a_specific_message()
    {
        using var folder = new TempFolder();
        var path = folder.Write("old.doc", BrokenFiles.Garbage());

        var result = await StubReaders().ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.LegacyFormatMessage);
    }

    [Fact]
    public async Task Missing_file_is_reported_without_the_path()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "missing.docx");

        var result = await StubReaders().ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.NotFoundMessage);
        Assert.DoesNotContain(folder.Path, result.Content);
    }

    [Fact]
    public async Task File_over_the_size_limit_is_rejected_before_opening()
    {
        using var folder = new TempFolder();
        var path = folder.Write("big.docx", new byte[100]);
        var fake = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => throw new InvalidOperationException("should not run"));
        var reader = Reader(new DocumentOptions { MaxFileBytes = 10 }, fake);

        var result = await reader.ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.TooLargeMessage);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Password_protected_openxml_is_reported_as_password_not_corrupt()
    {
        using var folder = new TempFolder();
        var path = folder.Write("locked.docx", BrokenFiles.PasswordProtectedOpenXml());
        var fake = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => throw new InvalidOperationException("should not run"));

        var result = await Reader(fake).ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.PasswordMessage);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Word_extension_is_dispatched_to_the_word_reader_case_insensitively()
    {
        using var folder = new TempFolder();
        var path = folder.Write("A.DOCX", new byte[] { 1 });
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => Ok("word text"));
        var excel = new FakeFormatReader(DocumentFormat.Excel, ".xlsx", (_, _) => Ok("excel text"));

        var result = await Reader(word, excel).ReadAsync(Request(path), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("word text", result.Content);
        Assert.StartsWith("<document name=\"", result.Content);
        Assert.Contains(DocumentOutput.DataNotice, result.Content);
        Assert.EndsWith("</document>", result.Content);
        Assert.Equal(DocumentFormat.Word, result.Format);
        Assert.Single(word.Requests);
        Assert.Empty(excel.Requests);
    }

    [Fact]
    public async Task Xlsm_is_dispatched_to_the_excel_reader()
    {
        using var folder = new TempFolder();
        var path = folder.Write("macro.xlsm", new byte[] { 1 });
        var excel = new FakeFormatReader(DocumentFormat.Excel, ".xlsm", (_, _) => Ok("sheet"));

        var result = await Reader(excel).ReadAsync(Request(path), CancellationToken.None);

        Assert.Contains("sheet", result.Content);
        Assert.Equal(DocumentFormat.Excel, result.Format);
    }

    [Fact]
    public async Task Selectors_are_passed_through_to_the_format_reader()
    {
        using var folder = new TempFolder();
        var path = folder.Write("b.xlsx", new byte[] { 1 });
        var excel = new FakeFormatReader(DocumentFormat.Excel, ".xlsx", (_, _) => Ok("x"));
        var request = new DocumentReadRequest(path, Sheet: "專案", Range: "A1:C3", IncludeNotes: false);

        await Reader(excel).ReadAsync(request, CancellationToken.None);

        var seen = Assert.Single(excel.Requests);
        Assert.Equal("專案", seen.Sheet);
        Assert.Equal("A1:C3", seen.Range);
        Assert.False(seen.IncludeNotes);
    }

    [Fact]
    public async Task Output_is_windowed_by_offset_and_max_chars()
    {
        using var folder = new TempFolder();
        var path = folder.Write("a.docx", new byte[] { 1 });
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => Ok("0123456789"));
        var request = new DocumentReadRequest(path, Offset: 2, MaxChars: 4);

        var result = await Reader(word).ReadAsync(request, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("\n2345\n</document>", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal(6, result.NextOffset);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task Reader_error_results_pass_through_unchanged()
    {
        using var folder = new TempFolder();
        var path = folder.Write("a.docx", new byte[] { 1 });
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => Task.FromResult(new DocumentReadResult(true, DocumentFormat.Word, "頁碼格式不正確", null, false, null)));

        var result = await Reader(word).ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, "頁碼格式不正確");
    }

    [Fact]
    public async Task Unexpected_exception_becomes_the_fixed_message_without_details()
    {
        using var folder = new TempFolder();
        var path = folder.Write("a.docx", new byte[] { 1 });
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => throw new IOException(@"C:\secret\internal.xml 壞掉了"));

        var result = await Reader(word).ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.FailedMessage);
        Assert.DoesNotContain("secret", result.Content);
        Assert.DoesNotContain("internal", result.Content);
    }

    [Fact]
    public async Task Exception_thrown_by_the_reader_before_any_await_is_converted_too()
    {
        using var folder = new TempFolder();
        var path = folder.Write("a.pdf", Pdf);
        var pdf = new FakeFormatReader(DocumentFormat.Pdf, ".pdf", (_, _) => throw new InvalidDataException("bad"));

        var result = await Reader(pdf).ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.FailedMessage);
    }

    [Fact]
    public async Task Document_rejected_exception_message_is_shown_as_is()
    {
        using var folder = new TempFolder();
        var path = folder.Write("bomb.docx", BrokenFiles.ZipBomb());
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => throw new DocumentRejectedException(OpenXmlSafety.RejectedMessage));

        var result = await Reader(word).ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, OpenXmlSafety.RejectedMessage);
    }

    [Fact]
    public async Task Encrypted_pdf_exception_is_reported_as_password()
    {
        using var folder = new TempFolder();
        var path = folder.Write("locked.pdf", Pdf);
        var pdf = new FakeFormatReader(DocumentFormat.Pdf, ".pdf", (_, _) => throw new PdfDocumentEncryptedException("encrypted"));

        var result = await Reader(pdf).ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.PasswordMessage);
    }

    [Fact]
    public async Task Reader_that_ignores_the_token_is_stopped_by_the_timeout()
    {
        using var folder = new TempFolder();
        var path = folder.Write("slow.docx", new byte[] { 1 });
        // 永遠不完成、也不看 ct：逾時要靠外層的 WaitAsync 讓呼叫端回來
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => new TaskCompletionSource<DocumentReadResult>().Task);
        var reader = Reader(new DocumentOptions { ReadTimeout = TimeSpan.FromMilliseconds(100) }, word);

        var result = await reader.ReadAsync(Request(path), CancellationToken.None);

        AssertError(result, DocumentReader.TimeoutMessage);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_turned_into_a_message()
    {
        using var folder = new TempFolder();
        var path = folder.Write("slow.docx", new byte[] { 1 });
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => new TaskCompletionSource<DocumentReadResult>().Task);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(word).ReadAsync(Request(path), cts.Token));
    }

    [Fact]
    public async Task Empty_content_is_a_valid_empty_result()
    {
        using var folder = new TempFolder();
        var path = folder.Write("empty.docx", new byte[] { 1 });
        var word = new FakeFormatReader(DocumentFormat.Word, ".docx", (_, _) => Ok(""));

        var result = await Reader(word).ReadAsync(Request(path), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("<document", result.Content);
        Assert.EndsWith("</document>", result.Content);   // 空內容仍然有完整的包裝
        Assert.False(result.Truncated);
        Assert.Null(result.NextOffset);
    }

    private static IDocumentReader StubReaders() => Reader(
        new WordDocumentReader(),
        new ExcelDocumentReader(),
        new PowerPointDocumentReader(),
        new PdfDocumentReader());

    private static DocumentReader Reader(params IDocumentFormatReader[] readers) => Reader(new DocumentOptions(), readers);

    private static DocumentReader Reader(DocumentOptions options, params IDocumentFormatReader[] readers) =>
        new(readers, options, NullLogger<DocumentReader>.Instance);

    private static DocumentReadRequest Request(string path) => new(path);

    private static Task<DocumentReadResult> Ok(string content) =>
        Task.FromResult(new DocumentReadResult(false, null, content, null, false, null));

    private static void AssertError(DocumentReadResult result, string message)
    {
        Assert.True(result.IsError);
        Assert.Equal(message, result.Content);
    }

    /// <summary>可以設定行為、記錄收到的請求的假讀取器。</summary>
    private sealed class FakeFormatReader(
        DocumentFormat format,
        string extension,
        Func<DocumentReadRequest, CancellationToken, Task<DocumentReadResult>> handler) : IDocumentFormatReader
    {
        public DocumentFormat Format { get; } = format;

        public List<DocumentReadRequest> Requests { get; } = [];

        public bool Supports(string ext) => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase);

        public Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return handler(request, ct);
        }
    }
}
