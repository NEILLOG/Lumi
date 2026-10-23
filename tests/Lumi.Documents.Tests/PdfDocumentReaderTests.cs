using System.Diagnostics;
using System.Text;
using Lumi.Core.Documents;
using Lumi.Documents.Tests.Samples;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Lumi.Documents.Tests;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名

public class PdfDocumentReaderTests
{
    private const string BlankPageMessage = "這一頁沒有可擷取的文字，可能是掃描圖片";

    [Fact]
    public async Task Default_read_returns_every_page_of_the_sample()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await new PdfDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(DocumentFormat.Pdf, result.Format);
        Assert.Equal("PDF · 共 3 頁（讀第 1～3 頁）", result.Summary);
        Assert.False(result.Truncated);
        Assert.Null(result.NextOffset);
        Assert.StartsWith("[概覽] 共 3 頁\n---", result.Content);
        Assert.Contains("## 第 1 頁\n", result.Content);
        Assert.Contains("## 第 2 頁\n", result.Content);
        Assert.Contains("## 第 3 頁\n", result.Content);
    }

    [Fact]
    public async Task Chinese_text_is_extracted_without_garbling()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "1");

        Assert.Contains("中文測試頁", result.Content);
        Assert.Contains("文件讀取", result.Content);
    }

    [Fact]
    public async Task English_text_is_extracted()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "2");

        Assert.Contains("English page: Lumi document reader.", result.Content);
    }

    [Fact]
    public async Task Blank_page_gets_the_scanned_page_message()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "3");

        Assert.Contains("## 第 3 頁\n（這一頁沒有可擷取的文字，可能是掃描圖片）", result.Content);
    }

    [Fact]
    public async Task Blank_page_with_an_image_mentions_the_image_count()
    {
        using var folder = new TempFolder();
        var path = folder.Write("image.pdf", BuildPdf([null], pageBuilder => pageBuilder.AddPng(OnePixelPng, new PdfRectangle(50, 50, 150, 150))));

        var result = await Read(path, "1");

        Assert.Contains("## 第 1 頁\n（這一頁沒有可擷取的文字，可能是掃描圖片，含 1 張圖片）", result.Content);
    }

    [Fact]
    public async Task Pages_selects_a_single_page()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "2");

        Assert.Contains("## 第 2 頁", result.Content);
        Assert.DoesNotContain("## 第 1 頁", result.Content);
        Assert.DoesNotContain("## 第 3 頁", result.Content);
        Assert.Equal("PDF · 共 3 頁（讀第 2～2 頁）", result.Summary);
        Assert.Contains("（共 3 頁，目前顯示第 2～2 頁，續讀請用 pages，例如 3-3）", result.Content);
    }

    [Fact]
    public async Task Pages_accepts_a_range_with_spaces_around_the_dash()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, " 2 - 3 ");

        Assert.DoesNotContain("## 第 1 頁", result.Content);
        Assert.Contains("## 第 2 頁", result.Content);
        Assert.Contains("## 第 3 頁", result.Content);
        Assert.Equal("PDF · 共 3 頁（讀第 2～3 頁）", result.Summary);
    }

    [Fact]
    public async Task Blank_or_missing_pages_argument_reads_from_the_first_page()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "   ");

        Assert.False(result.IsError);
        Assert.Contains("## 第 1 頁", result.Content);
        Assert.Contains("## 第 3 頁", result.Content);
    }

    [Fact]
    public async Task Range_end_past_the_last_page_is_clamped()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "2-99");

        Assert.False(result.IsError);
        Assert.Equal("PDF · 共 3 頁（讀第 2～3 頁）", result.Summary);
    }

    [Fact]
    public async Task Page_beyond_the_document_is_an_error_with_the_page_count()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "4");

        Assert.True(result.IsError);
        Assert.Contains("3 頁", result.Content);
        Assert.Null(result.Summary);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0-2")]
    public async Task Pages_below_one_is_an_error(string pages)
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, pages);

        Assert.True(result.IsError);
        Assert.Contains("從 1 開始", result.Content);
    }

    [Fact]
    public async Task Reversed_range_is_an_error()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, "3-2");

        Assert.True(result.IsError);
        Assert.Contains("不能小於", result.Content);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,2")]
    [InlineData("1-2-3")]
    [InlineData("-3")]
    public async Task Malformed_pages_is_an_error_that_shows_the_usage(string pages)
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);

        var result = await Read(path, pages);

        Assert.True(result.IsError);
        Assert.Contains("3 或 3-5", result.Content);
        Assert.Contains("共 3 頁", result.Content);
    }

    [Fact]
    public async Task Default_read_of_a_long_document_stops_at_fifty_pages_and_says_how_to_continue()
    {
        using var folder = new TempFolder();
        var path = folder.Write("long.pdf", LongDocument(200));

        var result = await Read(path, null);

        Assert.False(result.IsError);
        Assert.Contains("## 第 50 頁", result.Content);
        Assert.DoesNotContain("## 第 51 頁", result.Content);
        Assert.Contains("（共 200 頁，目前顯示第 1～50 頁，續讀請用 pages，例如 51-100）", result.Content);
        Assert.Equal("PDF · 共 200 頁（讀第 1～50 頁）", result.Summary);
    }

    [Fact]
    public async Task Continuation_range_reads_the_next_pages_and_points_further()
    {
        using var folder = new TempFolder();
        var path = folder.Write("long.pdf", LongDocument(200));

        var result = await Read(path, "51-100");

        Assert.DoesNotContain("## 第 50 頁", result.Content);
        Assert.Contains("## 第 51 頁", result.Content);
        Assert.Contains("## 第 100 頁", result.Content);
        Assert.DoesNotContain("## 第 101 頁", result.Content);
        Assert.Contains("續讀請用 pages，例如 101-150", result.Content);
    }

    [Fact]
    public async Task Last_pages_of_a_long_document_have_no_continuation_note()
    {
        using var folder = new TempFolder();
        var path = folder.Write("long.pdf", LongDocument(200));

        var result = await Read(path, "199-200");

        Assert.Contains("## 第 200 頁", result.Content);
        Assert.DoesNotContain("續讀", result.Content);
    }

    [Fact]
    public async Task Long_document_read_is_quick_because_only_selected_pages_are_parsed()
    {
        using var folder = new TempFolder();
        var path = folder.Write("long.pdf", LongDocument(200));
        var watch = Stopwatch.StartNew();

        var result = await Read(path, null);

        watch.Stop();
        Assert.False(result.IsError);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"耗時 {watch.Elapsed}");
    }

    [Fact]
    public async Task Page_text_longer_than_the_page_limit_is_cut_with_a_note()
    {
        using var folder = new TempFolder();
        var path = folder.Write("wide.pdf", BuildPdf(LongLines(PdfDocumentReader.MaxPageChars + 2_000)));

        var result = await Read(path, "1");

        Assert.False(result.IsError);
        Assert.Contains("（這一頁的文字超過 30,000 字元，已截斷）", result.Content);
        Assert.True(result.Content.Length < PdfDocumentReader.MaxPageChars + 500);
    }

    [Fact]
    public async Task Output_never_exceeds_the_total_limit_and_says_where_it_stopped()
    {
        using var folder = new TempFolder();
        var path = folder.Write("big.pdf", BuildPdf(Enumerable.Range(0, 20).Select(_ => LongLines(PdfDocumentReader.MaxPageChars - 1_000)).SelectMany(x => x)));

        var result = await Read(path, "1-20");

        Assert.False(result.IsError);
        Assert.True(result.Content.Length <= PdfDocumentReader.MaxTotalChars, $"輸出 {result.Content.Length} 字元");
        Assert.Contains("輸出已達 500,000 字元上限", result.Content);
        Assert.DoesNotContain("## 第 20 頁", result.Content);
    }

    [Fact]
    public async Task Overview_includes_title_and_author_when_present()
    {
        using var folder = new TempFolder();
        var path = folder.Write("info.pdf", BuildPdf(["第一頁"], title: "季度報告", author: "王小明"));

        var result = await Read(path, "1");

        Assert.Contains("[概覽] 共 1 頁\n標題：季度報告　作者：王小明\n---", result.Content);
    }

    [Fact]
    public async Task Overview_without_document_information_has_only_the_page_count()
    {
        using var folder = new TempFolder();
        var path = folder.Write("plain.pdf", BuildPdf(["第一頁"]));

        var result = await Read(path, "1");

        Assert.StartsWith("[概覽] 共 1 頁\n---", result.Content);
    }

    [Fact]
    public async Task Overly_long_title_is_cut_to_200_characters()
    {
        using var folder = new TempFolder();
        var title = new string('甲', 300);
        var path = folder.Write("long-title.pdf", BuildPdf(["第一頁"], title: title));

        var result = await Read(path, "1");

        var line = result.Content.Split('\n')[1];
        Assert.Equal("標題：" + new string('甲', 200), line);
    }

    [Fact]
    public async Task Lines_are_read_top_to_bottom_in_order()
    {
        using var folder = new TempFolder();
        var path = folder.Write("lines.pdf", BuildPdf(["第一行：甲乙丙\n第二行：丁戊己\n第三行：庚辛壬"]));

        var result = await Read(path, "1");

        var first = result.Content.IndexOf("第一行", StringComparison.Ordinal);
        var second = result.Content.IndexOf("第二行", StringComparison.Ordinal);
        var third = result.Content.IndexOf("第三行", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < second && second < third, result.Content);
    }

    [Fact]
    public void Normalize_collapses_blank_lines_and_trims_the_edges()
    {
        var text = "\n\n標題\n\n\n  \n內容  \r\n\r\n結尾\n\n";

        Assert.Equal("標題\n\n內容\n\n結尾", PdfDocumentReader.NormalizeText(text));
        Assert.Equal("", PdfDocumentReader.NormalizeText(" \n\t\n"));
    }

    [Fact]
    public async Task Corrupt_pdf_gives_the_fixed_failure_message_through_document_reader()
    {
        using var folder = new TempFolder();
        var path = folder.Write("corrupt.pdf", BrokenFiles.Garbage());

        var result = await ThroughDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.FailedMessage, result.Content);
    }

    [Fact]
    public async Task Empty_pdf_gives_the_fixed_failure_message_through_document_reader()
    {
        using var folder = new TempFolder();
        var path = folder.Write("empty.pdf", BrokenFiles.Empty());

        var result = await ThroughDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.FailedMessage, result.Content);
    }

    [Fact]
    public async Task Truncated_pdf_never_crashes_and_gives_either_text_or_the_fixed_message()
    {
        using var folder = new TempFolder();
        var full = PdfBuilder.CreateBytes();
        var path = folder.Write("truncated.pdf", full[..(full.Length / 2)]);

        var result = await ThroughDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        if (result.IsError)
            Assert.Equal(DocumentReader.FailedMessage, result.Content);
        else
            Assert.Contains("[概覽]", result.Content);
    }

    [Fact]
    public async Task Cancelled_token_stops_the_read()
    {
        using var folder = new TempFolder();
        var path = PdfBuilder.Create(folder.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfDocumentReader().ReadAsync(new DocumentReadRequest(path), cts.Token));
    }

    [Fact]
    public void Only_pdf_extension_is_supported()
    {
        var reader = new PdfDocumentReader();

        Assert.True(reader.Supports(".pdf"));
        Assert.True(reader.Supports(".PDF"));
        Assert.False(reader.Supports(".docx"));
        Assert.False(reader.Supports(".txt"));
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private static DocumentReader ThroughDocumentReader() =>
        new([new PdfDocumentReader()], new DocumentOptions(), NullLogger<DocumentReader>.Instance);

    private static Task<DocumentReadResult> Read(string path, string? pages) =>
        new PdfDocumentReader().ReadAsync(new DocumentReadRequest(path, Pages: pages), CancellationToken.None);

    private static byte[] LongDocument(int pageCount) =>
        BuildPdf(Enumerable.Range(1, pageCount).Select(page => (string?)$"第 {page} 頁的內容 page {page}"));

    /// <summary>
    /// 產生 PDF：每個字串是一頁（字串裡的換行是新的一行）；null 是空白頁。文字用打包的 Noto Sans TC。
    /// 可以額外在頁面上畫東西（例如圖片）。
    /// </summary>
    private static byte[] BuildPdf(
        IEnumerable<string?> pages,
        Action<PdfPageBuilder>? decorate = null,
        string? title = null,
        string? author = null)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(File.ReadAllBytes(PdfBuilder.FontPath));
        if (title is not null)
            builder.DocumentInformation.Title = title;
        if (author is not null)
            builder.DocumentInformation.Author = author;

        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text is not null)
            {
                var y = 780d;
                foreach (var line in text.Split('\n'))
                {
                    page.AddText(line, 12, new PdfPoint(50, y), font);
                    y -= 16;
                }
            }
            decorate?.Invoke(page);
        }

        return builder.Build();
    }

    /// <summary>約 <paramref name="chars"/> 個字元，切成每行 60 字（超過頁面高度的行也照樣寫進內容）。</summary>
    // 只用樣本字型確定有的字（Noto Sans TC 涵蓋常用字）
    private const string Cjk = "中文測試頁字體內容文件讀取段落標題作者";

    private static IEnumerable<string> LongLines(int chars)
    {
        var builder = new StringBuilder();
        var lines = new List<string>();
        for (var i = 0; i < chars; i++)
        {
            builder.Append(Cjk[i % Cjk.Length]);
            if (builder.Length == 60)
            {
                lines.Add(builder.ToString());
                builder.Clear();
            }
        }
        if (builder.Length > 0)
            lines.Add(builder.ToString());
        return [string.Join('\n', lines)];
    }
}
