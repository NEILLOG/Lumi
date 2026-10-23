using System.Text;
using System.Text.RegularExpressions;
using Lumi.Core.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Lumi.Documents;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名，這裡明確指向 Core 的列舉

/// <summary>
/// PDF（.pdf）讀取器（PdfPig，純 managed）。依頁輸出文字；只擷取被選取的頁面，所以大檔案的概覽不會解析全部頁面。
/// 沒有文字層的頁面（掃描圖片）只註明，不做 OCR。有密碼的檔案讓 <see cref="PdfDocumentEncryptedException"/> 往外丟，由 <see cref="DocumentReader"/> 處理。
/// </summary>
internal sealed class PdfDocumentReader : IDocumentFormatReader
{
    /// <summary>每一頁的文字上限（字元）；超過就截斷並說明。</summary>
    public const int MaxPageChars = 30_000;

    /// <summary>一次讀取的輸出上限（字元）。續讀由 <see cref="DocumentReader"/> 的 Offset／MaxChars 處理，這裡只限制單次產生的量。</summary>
    public const int MaxTotalChars = 500_000;

    private const int MaxInfoChars = 200;

    // 留給概覽與結尾說明的字元數：頁面內容只填到 MaxTotalChars - ReservedChars，整體仍不超過上限
    private const int ReservedChars = 1_000;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    public DocumentFormat Format => DocumentFormat.Pdf;

    public bool Supports(string extension) => string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        // 同步執行：DocumentReader 會把它放到背景執行緒並套用逾時；例外由 DocumentReader 統一轉成固定訊息
        return Task.FromResult(Read(request, ct));
    }

    private static DocumentReadResult Read(DocumentReadRequest request, CancellationToken ct)
    {
        // 唯讀開啟並允許別的程式同時開著；PdfPig 讀取這個串流期間檔案保持開啟
        using var stream = new FileStream(request.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        ct.ThrowIfCancellationRequested();

        var total = document.NumberOfPages;

        // 沒給 pages 就從第 1 頁開始，最多 PageRange.MaxSpan 頁
        (int From, int To) range = (1, Math.Min(total, PageRange.MaxSpan));
        if (!string.IsNullOrWhiteSpace(request.Pages)
            && !PageRange.TryParse(request.Pages, total, out range, out var error))
        {
            return Error($"{error}這份 PDF 共 {total} 頁。");
        }

        var output = new StringBuilder();
        output.Append("[概覽] 共 ").Append(total).Append(" 頁");
        if (DocumentInfoLine(document) is { } info)
            output.Append('\n').Append(info);
        output.Append("\n---");

        var budget = MaxTotalChars - ReservedChars;
        var shownTo = range.From - 1;
        int? stoppedAt = null;

        for (var page = range.From; page <= range.To; page++)
        {
            ct.ThrowIfCancellationRequested();

            var piece = "\n\n## 第 " + page + " 頁\n" + PageBody(document.GetPage(page));
            var room = budget - output.Length;
            if (piece.Length > room)
            {
                // 只放得下一部分：截到剛好（不切斷代理對），之後的頁面不再讀
                var cut = Math.Max(0, room);
                if (cut > 0 && char.IsHighSurrogate(piece[cut - 1]))
                    cut--;
                output.Append(piece, 0, cut);
                if (cut > 0)
                    shownTo = page;
                stoppedAt = page;
                break;
            }

            output.Append(piece);
            shownTo = page;
        }

        if (stoppedAt is { } stopped)
        {
            output.Append("\n\n（輸出已達 ").Append(MaxTotalChars.ToString("N0")).Append(" 字元上限，第 ")
                .Append(stopped).Append(" 頁之後未完整顯示，請用 pages 指定較少的頁數）");
        }
        else if (range.To < total)
        {
            var nextTo = Math.Min(total, range.To + PageRange.MaxSpan);
            output.Append("\n\n（共 ").Append(total).Append(" 頁，目前顯示第 ").Append(range.From).Append("～").Append(range.To)
                .Append(" 頁，續讀請用 pages，例如 ").Append(range.To + 1).Append("-").Append(nextTo).Append("）");
        }

        var summary = shownTo >= range.From
            ? $"PDF · 共 {total} 頁（讀第 {range.From}～{shownTo} 頁）"
            : $"PDF · 共 {total} 頁";

        return new DocumentReadResult(false, DocumentFormat.Pdf, output.ToString(), summary, false, null);
    }

    /// <summary>單頁的內容：有文字就整理後回傳；沒有就回說明（有圖片時註明張數）。</summary>
    private static string PageBody(Page page)
    {
        var text = NormalizeText(ContentOrderTextExtractor.GetText(page, addDoubleNewline: false));
        if (text.Length == 0)
        {
            var images = page.GetImages().Count();
            return images > 0
                ? $"（這一頁沒有可擷取的文字，可能是掃描圖片，含 {images} 張圖片）"
                : "（這一頁沒有可擷取的文字，可能是掃描圖片）";
        }

        if (text.Length <= MaxPageChars)
            return text;

        return TruncateAt(text, MaxPageChars) + "\n（這一頁的文字超過 30,000 字元，已截斷）";
    }

    /// <summary>標題與作者（各最多 200 字元，空的不寫）；兩個都沒有就回 null。</summary>
    private static string? DocumentInfoLine(PdfDocument document)
    {
        var information = document.Information;
        var parts = new List<string>();

        var title = CleanInfo(information?.Title);
        if (title.Length > 0)
            parts.Add("標題：" + title);

        var author = CleanInfo(information?.Author);
        if (author.Length > 0)
            parts.Add("作者：" + author);

        return parts.Count == 0 ? null : string.Join("　", parts);
    }

    private static string CleanInfo(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : TruncateAt(Whitespace.Replace(value, " ").Trim(), MaxInfoChars);

    /// <summary>
    /// 去掉每行的行尾空白、把連續空行壓成一個，並去掉開頭與結尾的空行。
    /// 全部是空白時回傳空字串。
    /// </summary>
    internal static string NormalizeText(string text)
    {
        var kept = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 && (kept.Count == 0 || kept[^1].Length == 0))
                continue;
            kept.Add(line);
        }

        while (kept.Count > 0 && kept[^1].Length == 0)
            kept.RemoveAt(kept.Count - 1);

        return string.Join('\n', kept);
    }

    /// <summary>取前 <paramref name="max"/> 個字元；不會把代理對切成兩半。</summary>
    private static string TruncateAt(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut];
    }

    private static DocumentReadResult Error(string message) => new(true, DocumentFormat.Pdf, message, null, false, null);
}
