using System.Text;
using System.Xml;
using System.Xml.Linq;
using Lumi.Core.Documents;
using global::DocumentFormat.OpenXml.Packaging;

namespace Lumi.Documents;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名，這裡明確指向 Core 的列舉

/// <summary>
/// PowerPoint（.pptx）讀取器。投影片順序依 presentation.xml 的 sldIdLst；每張的標題、內文（含表格與清單）、圖片與圖表張數、講者備註。
/// 概覽列出前 100 張的標題；內文最多讀 <see cref="PageRange.MaxSpan"/> 張（省略 slides 時從第 1 張開始）。
/// </summary>
internal sealed class PowerPointDocumentReader : IDocumentFormatReader
{
    private const int MaxOverviewLines = 100;
    private const int MaxOverviewTitleChars = 100;
    private const int MaxSlideChars = 20_000;
    private const int MaxOutputChars = 500_000;

    // 留給最後的說明行（續讀說明、達到上限的說明）；正文的上限要扣掉這一段
    private const int NoticeReserveChars = 400;

    private const string NoSlideTextLine = "（這張投影片沒有文字）";
    private const string UntitledText = "（無標題）";
    private const string HiddenSuffix = "（已隱藏）";

    private static readonly XNamespace P = PowerPointSlideText.P;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public DocumentFormat Format => DocumentFormat.PowerPoint;

    public bool Supports(string extension) => string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct) =>
        Task.FromResult(Read(request, ct));

    private DocumentReadResult Read(DocumentReadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        OpenXmlSafety.Verify(request.FullPath);
        using var stream = new FileStream(request.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = PresentationDocument.Open(stream, isEditable: false, OpenXmlSafety.CreateOpenSettings());

        var presentationPart = document.PresentationPart ?? throw new InvalidDataException("簡報缺少主要部件。");
        var slides = SlideParts(presentationPart);
        var total = slides.Count;

        var (from, to) = (1, Math.Min(total, PageRange.MaxSpan));
        if (!string.IsNullOrWhiteSpace(request.Slides))
        {
            if (!PageRange.TryParse(request.Slides, total, out var range, out var detail))
                return Error(UsageMessage(total, detail));
            (from, to) = range;
        }

        // 同一張投影片可能同時出現在概覽與內文，只解析一次
        var loaded = new Dictionary<int, PowerPointSlideContent>();
        PowerPointSlideContent Slide(int number) =>
            loaded.TryGetValue(number, out var cached)
                ? cached
                : loaded[number] = PowerPointSlideText.ParseSlide(LoadXml(slides[number - 1]));

        var overview = BuildOverview(total, Slide);
        var body = new StringBuilder();
        var lastRead = from - 1;
        var sizeLimited = false;

        for (var number = from; number <= to; number++)
        {
            ct.ThrowIfCancellationRequested();

            var notes = request.IncludeNotes ? LoadNotesText(slides[number - 1]) : null;
            var section = BuildSection(number, Slide(number), notes);

            var separator = body.Length == 0 ? 0 : 2;
            if (overview.Length + body.Length + separator + section.Length + NoticeReserveChars > MaxOutputChars)
            {
                sizeLimited = true;
                break;
            }

            if (separator > 0)
                body.Append("\n\n");
            body.Append(section);
            lastRead = number;
        }

        var output = new StringBuilder(overview);
        if (body.Length > 0)
            output.Append('\n').Append(body);

        var notices = new List<string>();
        if (sizeLimited)
            notices.Add($"（輸出已達 {MaxOutputChars:N0} 字元上限，後面的投影片沒有列出。）");
        if (lastRead < total)
            notices.Add($"（本次讀取第 {from}–{lastRead} 張，共 {total} 張。要讀其他張請用 slides 指定，例如 {lastRead + 1}-{Math.Min(total, lastRead + PageRange.MaxSpan)}。）");
        if (notices.Count > 0)
            output.Append("\n\n").Append(string.Join("\n", notices));

        return new DocumentReadResult(false, Format, output.ToString().TrimEnd(), $"PowerPoint · {total} 張投影片", false, null);
    }

    private static string UsageMessage(int total, string detail) =>
        $"slides 參數不正確：{detail}這份簡報共 {total} 張投影片，請用 3 或 3-5 的形式指定（從 1 開始，一次最多 {PageRange.MaxSpan} 張）。";

    /// <summary>概覽：總張數、前 100 張的標題（隱藏的加註）、超過時的「…另有 N 張」。最後一行是 <c>---</c>。</summary>
    private static string BuildOverview(int total, Func<int, PowerPointSlideContent> slide)
    {
        var builder = new StringBuilder();
        builder.Append($"[概覽] 共 {total} 張投影片\n");

        var listed = Math.Min(total, MaxOverviewLines);
        for (var number = 1; number <= listed; number++)
        {
            var content = slide(number);
            var title = content.Title.Length == 0 ? UntitledText : PowerPointSlideText.Clip(content.Title, MaxOverviewTitleChars);
            builder.Append($"- {number}. {title}{(content.Hidden ? HiddenSuffix : "")}\n");
        }

        if (total > listed)
            builder.Append($"…另有 {total - listed} 張\n");
        builder.Append("---\n");
        return builder.ToString();
    }

    /// <summary>一張投影片的段落：標題、內文、圖片與圖表張數、講者備註。超過單張上限時截斷並說明。</summary>
    private static string BuildSection(int number, PowerPointSlideContent slide, string? notes)
    {
        var builder = new StringBuilder();
        builder.Append($"## 投影片 {number}：{(slide.Title.Length == 0 ? UntitledText : slide.Title)}\n");

        if (slide.BodyLines.Count == 0)
            builder.Append(NoSlideTextLine).Append('\n');
        foreach (var line in slide.BodyLines)
            builder.Append(line).Append('\n');

        if (slide.Pictures > 0)
            builder.Append($"（含 {slide.Pictures} 張圖片）\n");
        if (slide.Charts > 0)
            builder.Append($"（含 {slide.Charts} 個圖表）\n");

        if (notes is not null)
            builder.Append("\n[講者備註]\n").Append(notes).Append('\n');

        var text = builder.ToString().TrimEnd('\n');
        if (text.Length <= MaxSlideChars)
            return text;

        const string notice = "\n…（這張投影片超過 20,000 字元，已截斷）";
        var cut = MaxSlideChars - notice.Length;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + notice;
    }

    /// <summary>依 presentation.xml 的 sldIdLst 順序取出投影片部件。</summary>
    private static List<SlidePart> SlideParts(PresentationPart presentationPart)
    {
        var presentation = LoadXml(presentationPart);
        var ids = presentation.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId")
            .Select(id => id.Attribute(R + "id")?.Value)
            .ToList() ?? [];

        return ids.Select(id =>
                (id is null ? null : presentationPart.GetPartById(id)) as SlidePart
                ?? throw new InvalidDataException("簡報的投影片關聯遺失。"))
            .ToList();
    }

    private static string? LoadNotesText(SlidePart slide) =>
        slide.NotesSlidePart is { } notesPart ? PowerPointSlideText.ParseNotes(LoadXml(notesPart)) : null;

    private static XDocument LoadXml(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, OpenXmlSafety.CreateXmlReaderSettings());
        return XDocument.Load(reader);
    }

    private static DocumentReadResult Error(string message) => new(true, DocumentFormat.PowerPoint, message, null, false, null);
}
