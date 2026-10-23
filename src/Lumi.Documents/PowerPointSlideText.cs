using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Lumi.Documents;

/// <summary>一張投影片解析後的內容（不含講者備註；備註由 <see cref="PowerPointSlideText.ParseNotes"/> 另外取得）。</summary>
internal sealed record PowerPointSlideContent(string Title, bool Hidden, IReadOnlyList<string> BodyLines, int Pictures, int Charts);

/// <summary>
/// 把投影片與講者備註的 XML 轉成文字（M10 C5）。走訪順序就是 spTree 的文件順序；只處理 p:sp、p:grpSp（遞迴）、p:graphicFrame（表格與圖表）與 p:pic（只計數）。
/// 純函式，不碰檔案；XML 由呼叫端用有字元上限的 XmlReader 載入。
/// </summary>
internal static class PowerPointSlideText
{
    internal static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    internal static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>表格最多輸出的列數（含表頭）。</summary>
    public const int MaxTableRows = 100;

    /// <summary>單一儲存格的字元上限（含結尾的「…」）。</summary>
    public const int MaxCellChars = 500;

    private const int MaxListLevel = 8;

    private static readonly HashSet<string> TitleTypes = ["title", "ctrTitle"];

    // 講者備註不要縮圖與頁碼之類的佔位符（縮圖是圖片，頁碼與頁首頁尾是版面用的）
    private static readonly HashSet<string> NotesSkipTypes = ["sldImg", "sldNum", "hdr", "ftr", "dt"];

    public static PowerPointSlideContent ParseSlide(XDocument slide)
    {
        var root = slide.Root;
        var hidden = root?.Attribute("show")?.Value is "0" or "false";
        var spTree = root?.Element(P + "cSld")?.Element(P + "spTree");
        if (spTree is null)
            return new PowerPointSlideContent("", hidden, [], 0, 0);

        var titleShape = spTree.Elements(P + "sp").FirstOrDefault(s => IsTitle(PlaceholderType(s)));
        var walker = new Walker(titleShape, skipNotesPlaceholders: false);
        walker.Walk(spTree.Elements());

        var title = titleShape is null ? "" : TextOf(titleShape);
        return new PowerPointSlideContent(title, hidden, Tidy(walker.Lines), walker.Pictures, walker.Charts);
    }

    /// <summary>講者備註的文字；沒有備註或備註是空的回 null。</summary>
    public static string? ParseNotes(XDocument notes)
    {
        var spTree = notes.Root?.Element(P + "cSld")?.Element(P + "spTree");
        if (spTree is null)
            return null;

        var walker = new Walker(titleShape: null, skipNotesPlaceholders: true);
        walker.Walk(spTree.Elements());

        var lines = Tidy(walker.Lines);
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>超過上限時截到 <paramref name="max"/> 個字元（含「…」），不切斷代理對。</summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + "…";
    }

    private static string? PlaceholderType(XElement shape)
    {
        var ph = shape.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
        if (ph is null)
            return null;
        // ph 沒有 type 屬性時，規格的預設值是 obj（內容）
        return ph.Attribute("type")?.Value ?? "obj";
    }

    private static bool IsTitle(string? type) => type is not null && TitleTypes.Contains(type);

    /// <summary>形狀內所有段落的文字，以空白連起來（標題用）。</summary>
    private static string TextOf(XElement shape)
    {
        var paragraphs = shape.Element(P + "txBody")?.Elements(A + "p").Select(ParagraphText).Where(t => t.Length > 0) ?? [];
        return string.Join(" ", paragraphs);
    }

    /// <summary>段落的純文字：a:t 原樣，a:br（軟換行）變成空白；超連結與欄位只取顯示文字。</summary>
    private static string ParagraphText(XElement paragraph)
    {
        var builder = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == A + "t")
                builder.Append(node.Value);
            else if (node.Name == A + "br")
                builder.Append(' ');
        }
        return builder.ToString().Trim();
    }

    /// <summary>有明確項目符號（buChar、buAutoNum、buBlip）的段落才算清單；buNone 與沒有項目符號屬性的都是一般文字。</summary>
    private static bool HasBullet(XElement? properties) =>
        properties is not null &&
        (properties.Element(A + "buChar") is not null ||
         properties.Element(A + "buAutoNum") is not null ||
         properties.Element(A + "buBlip") is not null);

    private static int ListLevel(XElement? properties)
    {
        var raw = properties?.Attribute("lvl")?.Value;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
            return 0;
        return Math.Clamp(level, 0, MaxListLevel);
    }

    /// <summary>去掉開頭與結尾的空行，並把連續的空行壓成一行。</summary>
    private static List<string> Tidy(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (result.Count == 0 || result[^1].Length == 0))
                continue;
            result.Add(line);
        }
        while (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>
    /// 依文件順序走訪形狀。<paramref name="titleShape"/> 是要略過的標題佔位符（它已經當成標題用了）；
    /// <paramref name="skipNotesPlaceholders"/> 為 true 時略過備註裡的縮圖、頁碼等佔位符。
    /// </summary>
    private sealed class Walker(XElement? titleShape, bool skipNotesPlaceholders)
    {
        public List<string> Lines { get; } = [];
        public int Pictures { get; private set; }
        public int Charts { get; private set; }

        public void Walk(IEnumerable<XElement> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Name == P + "sp")
                    HandleShape(node);
                else if (node.Name == P + "grpSp")
                    Walk(node.Elements());
                else if (node.Name == P + "graphicFrame")
                    HandleFrame(node);
                else if (node.Name == P + "pic")
                    Pictures++;
            }
        }

        private void HandleShape(XElement shape)
        {
            if (ReferenceEquals(shape, titleShape))
                return;

            var type = PlaceholderType(shape);
            if (skipNotesPlaceholders && type is not null && NotesSkipTypes.Contains(type))
                return;

            var paragraphs = shape.Element(P + "txBody")?.Elements(A + "p");
            if (paragraphs is null)
                return;

            foreach (var paragraph in paragraphs)
                AddParagraph(paragraph);
        }

        private void AddParagraph(XElement paragraph)
        {
            var text = ParagraphText(paragraph);
            if (text.Length == 0)
                return;

            var properties = paragraph.Element(A + "pPr");
            if (!HasBullet(properties))
            {
                Lines.Add(text);
                return;
            }

            Lines.Add(new string(' ', ListLevel(properties) * 2) + "- " + text);
        }

        private void HandleFrame(XElement frame)
        {
            var data = frame.Element(A + "graphic")?.Element(A + "graphicData");
            if (data is null)
                return;

            var table = data.Element(A + "tbl");
            if (table is not null)
            {
                AddTable(table);
                return;
            }

            // 圖表與 SmartArt（圖表的 uri 結尾是 /chart，SmartArt 是 /diagram）只計數，不輸出內容
            var uri = data.Attribute("uri")?.Value ?? "";
            if (uri.EndsWith("/chart", StringComparison.Ordinal) || uri.EndsWith("/diagram", StringComparison.Ordinal))
                Charts++;
        }

        private void AddTable(XElement table)
        {
            var rows = table.Elements(A + "tr").ToList();
            if (rows.Count == 0)
                return;

            var shown = rows.Take(MaxTableRows).Select(row => row.Elements(A + "tc").Select(CellText).ToList()).ToList();
            var columns = Math.Max(1, shown.Max(row => row.Count));

            // 表格前後空一行，免得 Markdown 把前後的文字併進表格
            Lines.Add("");
            Lines.Add(MarkdownRow(shown[0], columns));
            Lines.Add(MarkdownRow(Enumerable.Repeat("---", columns).ToList(), columns));
            foreach (var row in shown.Skip(1))
                Lines.Add(MarkdownRow(row, columns));

            if (rows.Count > MaxTableRows)
                Lines.Add($"（表格還有 {rows.Count - MaxTableRows} 列未顯示）");
            Lines.Add("");
        }

        private static string CellText(XElement cell)
        {
            var paragraphs = cell.Element(A + "txBody")?.Elements(A + "p").Select(ParagraphText).Where(t => t.Length > 0) ?? [];
            var text = Clip(string.Join("\n", paragraphs), MaxCellChars);
            return text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
        }

        private static string MarkdownRow(IReadOnlyList<string> cells, int columns)
        {
            var padded = Enumerable.Range(0, columns).Select(i => i < cells.Count ? cells[i] : "");
            return "| " + string.Join(" | ", padded) + " |";
        }
    }
}
