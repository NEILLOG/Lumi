using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lumi.Core.Documents;

namespace Lumi.Documents;

/// <summary>
/// Word（.docx）讀取器（M10 C3）：依文件順序走訪本文的段落與表格，輸出 Markdown 風格的純文字，最前面附結構概覽。
/// 只讀主文件的本文；頁首頁尾、註腳、批註與刪除的修訂都不在本文內，因此不輸出；插入的修訂照常輸出。
/// 文字方塊（mc:AlternateContent 的 Choice，或 VML 的 w:txbxContent）放在所在段落之後，以 <c>[文字方塊] </c> 開頭；Fallback 的備援副本略過。
/// </summary>
internal sealed class WordDocumentReader : IDocumentFormatReader
{
    /// <summary>主文件部件（word/document.xml）解壓後的大小上限（40 MB）。超過就不載入 OpenXml 型別樹。</summary>
    internal const long DefaultMaxMainPartBytes = 40L * 1024 * 1024;
    internal const string MainPartTooLargeMessage = "這份文件的內容過大，已拒絕讀取。";
    internal const string EmptyMessage = "（這份文件沒有文字內容）";
    internal const int MaxBodyChars = 500_000;
    internal const int MaxOutlineHeadings = 40;
    internal const int MaxTableRows = 200;
    internal const int MaxCellChars = 500;
    internal const string TextBoxPrefix = "[文字方塊] ";

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string TextBoxElement = "txbxContent";
    private const string FallbackElement = "Fallback";
    private const int MaxListLevel = 8;

    private readonly long _maxMainPartBytes;

    private static readonly Regex HeadingPattern = new(@"^\s*(?:heading|標題)\s*([1-9])\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex TitlePattern = new(@"^\s*(?:title|標題)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    public Lumi.Core.Documents.DocumentFormat Format => Lumi.Core.Documents.DocumentFormat.Word;

    public bool Supports(string extension) => string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase);

    /// <param name="maxMainPartBytes">主文件部件解壓後的上限；預設 40 MB，測試可以改小。</param>
    public WordDocumentReader(long maxMainPartBytes = DefaultMaxMainPartBytes)
    {
        _maxMainPartBytes = maxMainPartBytes;
    }

    public Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 整份檔案只讀一次，之後的 zip 檢查、部件大小檢查與開啟都用同一份位元組，避免檢查之後檔案被改寫
        byte[] bytes;
        using (var file = new FileStream(request.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var copy = new MemoryStream())
        {
            file.CopyTo(copy);
            bytes = copy.ToArray();
        }

        using (var verify = new MemoryStream(bytes, writable: false))
            OpenXmlSafety.Verify(verify);

        using var stream = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(stream, isEditable: false, OpenXmlSafety.CreateOpenSettings());
        var main = document.MainDocumentPart ?? throw new InvalidDataException("缺少主文件部件。");

        // 宣告大小（zip 目錄）可偽造，所以這裡實際解壓並計算位元組數，超過就拒絕；OpenXml 要到 Walker 取 Document 時才解析這個部件
        VerifyMainPartSize(bytes, main.Uri.OriginalString.TrimStart('/'), ct);

        var walker = new Walker(main, ct);
        walker.Walk();
        return Task.FromResult(new DocumentReadResult(
            IsError: false,
            Format: Format,
            Content: walker.BuildContent(),
            Summary: $"Word · {walker.Paragraphs} 段落",
            Truncated: false,
            NextOffset: null));
    }

    /// <summary>實際解壓主文件部件並計算位元組數；超過上限丟 <see cref="DocumentRejectedException"/>（訊息固定）。</summary>
    private void VerifyMainPartSize(byte[] bytes, string partName, CancellationToken ct)
    {
        using var zipStream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(partName) ?? throw new InvalidDataException("缺少主文件部件。");

        using var part = entry.Open();
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = part.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (total > _maxMainPartBytes)
                throw new DocumentRejectedException(MainPartTooLargeMessage);
        }
    }

    /// <summary>單次讀取的狀態：樣式表、計數、大綱與本文區塊。只在 <see cref="ReadAsync"/> 內使用。</summary>
    private sealed class Walker
    {
        private readonly MainDocumentPart _main;
        private readonly CancellationToken _ct;
        private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
        private readonly List<string> _outline = [];
        private readonly StringBuilder _body = new();
        private bool _lastWasListItem;
        private bool _truncated;

        public Walker(MainDocumentPart main, CancellationToken ct)
        {
            _main = main;
            _ct = ct;
        }

        public int Paragraphs { get; private set; }
        public int Tables { get; private set; }
        public int Headings { get; private set; }

        public void Walk()
        {
            foreach (var style in _main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            {
                if (style.StyleId?.Value is { } id)
                    _styles[id] = style;
            }

            if (_main.Document?.Body is { } body)
                WalkBlocks(body);
        }

        /// <summary>概覽、大綱、分隔線與本文；本文超過上限時已在 <see cref="Add"/> 截斷並記下。</summary>
        public string BuildContent()
        {
            var builder = new StringBuilder();
            builder.Append("[概覽] 段落 ").Append(Paragraphs)
                .Append("、表格 ").Append(Tables)
                .Append("、標題 ").Append(Headings).Append('\n');

            if (_outline.Count > 0)
            {
                builder.Append("大綱：\n");
                foreach (var line in _outline)
                    builder.Append(line).Append('\n');
                if (Headings > _outline.Count)
                    builder.Append("…另有 ").Append(Headings - _outline.Count).Append(" 個標題\n");
            }

            builder.Append("---\n");
            if (_body.Length == 0)
            {
                builder.Append(EmptyMessage);
            }
            else
            {
                builder.Append(_body);
                if (_truncated)
                    builder.Append("\n\n（內文超過 500,000 字元，後面的內容未讀取。）");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 依順序處理本文的區塊；sdt（內容控制項）與 customXml 只是外殼，裡面照樣走訪。
        /// 元素一律以「命名空間＋本地名稱」比對，不靠型別：mc:AlternateContent 裡的內容 SDK 讀成未知元素，型別比對會漏掉。
        /// </summary>
        private void WalkBlocks(OpenXmlElement container)
        {
            foreach (var child in container.ChildElements)
            {
                _ct.ThrowIfCancellationRequested();
                if (Is(child, "p"))
                {
                    AddParagraph(child);
                    AddTextBoxes(child);
                }
                else if (Is(child, "tbl"))
                {
                    Tables++;
                    foreach (var block in RenderTable(child))
                        Add(block, isListItem: false);
                    AddTextBoxes(child);
                }
                else if (Is(child, "sdt") && child.ChildElements.FirstOrDefault(e => Is(e, "sdtContent")) is { } content)
                {
                    WalkBlocks(content);
                }
                else if (Is(child, "customXml"))
                {
                    WalkBlocks(child);
                }
                else
                {
                    // 其他區塊（例如 mc:AlternateContent、sectPr）只找文字方塊
                    AddTextBoxes(child);
                }
            }
        }

        private void AddParagraph(OpenXmlElement paragraph)
        {
            var properties = paragraph.GetFirstChild<ParagraphProperties>();
            var styleId = properties?.ParagraphStyleId?.Val?.Value;
            var style = FindStyle(styleId);
            var text = ParagraphText(paragraph);
            if (text.Length == 0)
                return;

            Paragraphs++;

            var level = HeadingLevel(styleId, style?.StyleName?.Val?.Value);
            if (level > 0)
            {
                // 標題只放一行：段落裡的換行（Break）改成空白，大綱與本文用同一行
                var heading = new string('#', Math.Min(level, 6)) + " " + text.Replace('\n', ' ');
                Headings++;
                if (_outline.Count < MaxOutlineHeadings)
                    _outline.Add(heading);
                Add(heading, isListItem: false);
                return;
            }

            var listLevel = ListLevel(properties, style);
            if (listLevel is int indent)
                Add(new string(' ', indent * 2) + "- " + text, isListItem: true);
            else
                Add(text, isListItem: false);
        }

        /// <summary>
        /// 找出這個區塊裡的文字方塊（任何深度），略過 Fallback 裡的備援副本。
        /// 文字方塊內的段落不套用標題與清單，只加上前綴。
        /// </summary>
        private void AddTextBoxes(OpenXmlElement root)
        {
            foreach (var element in root.Descendants())
            {
                if (!Is(element, TextBoxElement) || IsInsideFallback(element))
                    continue;

                foreach (var block in TextBoxBlocks(element))
                    Add(block, isListItem: false);
            }
        }

        private IEnumerable<string> TextBoxBlocks(OpenXmlElement textBox)
        {
            foreach (var child in textBox.ChildElements)
            {
                if (Is(child, "p"))
                {
                    var text = ParagraphText(child);
                    if (text.Length > 0)
                        yield return TextBoxPrefix + text;
                }
                else if (Is(child, "tbl"))
                {
                    foreach (var block in RenderTable(child))
                        yield return block;
                }
            }
        }

        /// <summary>
        /// 把表格轉成 Markdown 表格（第一列當表頭）。超過列數上限或本文已到上限時只輸出前面的列，並加說明。
        /// 缺的儲存格補空字串；回傳的第一個區塊是表格本身，之後可能有一行說明。
        /// </summary>
        private List<string> RenderTable(OpenXmlElement table)
        {
            var result = new List<string>();
            var rows = table.ChildElements.Where(e => Is(e, "tr")).ToList();
            var shown = new List<List<string>>();
            long textSize = 0;

            foreach (var row in rows.Take(MaxTableRows))
            {
                _ct.ThrowIfCancellationRequested();
                var cells = row.ChildElements.Where(e => Is(e, "tc")).Select(CellText).ToList();
                shown.Add(cells);
                textSize += cells.Sum(c => (long)c.Length + 3);
                // 單一表格不可能比本文上限還大；超過就停，避免病態的表格吃光記憶體
                if (textSize > MaxBodyChars)
                    break;
            }

            var columns = shown.Count == 0 ? 0 : shown.Max(r => r.Count);
            if (columns > 0)
            {
                var lines = new List<string>
                {
                    MarkdownRow(shown[0], columns),
                    "| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |",
                };
                lines.AddRange(shown.Skip(1).Select(r => MarkdownRow(r, columns)));
                result.Add(string.Join("\n", lines));
            }

            if (shown.Count < rows.Count)
                result.Add($"（表格共 {rows.Count} 列，這裡只顯示前 {shown.Count} 列）");

            return result;
        }

        private static string MarkdownRow(List<string> cells, int columns)
        {
            var padded = cells.Concat(Enumerable.Repeat(string.Empty, columns - cells.Count));
            return "| " + string.Join(" | ", padded.Select(c => c.Replace("|", "\\|", StringComparison.Ordinal))) + " |";
        }

        /// <summary>儲存格內容：多個段落以 &lt;br&gt; 連接，巢狀表格壓成單行，最後截到 500 字元。</summary>
        private static string CellText(OpenXmlElement cell)
        {
            var parts = new List<string>();
            foreach (var child in cell.ChildElements)
            {
                if (Is(child, "p"))
                {
                    var text = ParagraphText(child);
                    if (text.Length > 0)
                        parts.Add(text.Replace("\n", "<br>", StringComparison.Ordinal));
                }
                else if (Is(child, "tbl"))
                {
                    var flat = FlattenTable(child);
                    if (flat.Length > 0)
                        parts.Add(flat);
                }
            }

            var joined = string.Join("<br>", parts);
            if (joined.Length <= MaxCellChars)
                return joined;

            var cut = MaxCellChars;
            if (char.IsHighSurrogate(joined[cut - 1]))
                cut--;
            return joined[..cut] + "…";
        }

        /// <summary>巢狀表格：列以「; 」連接，同一列的儲存格以「 / 」連接。</summary>
        private static string FlattenTable(OpenXmlElement table)
        {
            var rows = table.ChildElements
                .Where(e => Is(e, "tr"))
                .Take(MaxTableRows)
                .Select(row => string.Join(" / ", row.ChildElements.Where(e => Is(e, "tc")).Select(CellText).Where(s => s.Length > 0)))
                .Where(s => s.Length > 0);
            return string.Join("; ", rows);
        }

        /// <summary>加入一個本文區塊（段落或表格）。清單項目之間只空一行換行，其他區塊之間空一行。超過上限就截斷並停止加入。</summary>
        private void Add(string text, bool isListItem)
        {
            if (_truncated || text.Length == 0)
                return;

            if (_body.Length > 0)
                _body.Append(isListItem && _lastWasListItem ? "\n" : "\n\n");
            _body.Append(text);
            _lastWasListItem = isListItem;

            if (_body.Length > MaxBodyChars)
            {
                var cut = MaxBodyChars;
                if (char.IsHighSurrogate(_body[cut - 1]))
                    cut--;
                _body.Length = cut;
                _truncated = true;
            }
        }

        private Style? FindStyle(string? styleId) =>
            styleId is not null && _styles.TryGetValue(styleId, out var style) ? style : null;

        /// <summary>標題層級：Title 或 Heading1～9（樣式 id 或名稱，英文與中文「標題 N」都認）；不是標題回 0。</summary>
        private static int HeadingLevel(string? styleId, string? styleName)
        {
            foreach (var candidate in new[] { styleId, styleName })
            {
                if (string.IsNullOrEmpty(candidate))
                    continue;
                if (TitlePattern.IsMatch(candidate))
                    return 1;
                var match = HeadingPattern.Match(candidate);
                if (match.Success)
                    return match.Groups[1].Value[0] - '0';
            }

            return 0;
        }

        /// <summary>清單層級（0 起算）；沒有有效的 numPr（或 numId 為 0）回 null。段落沒有寫 numPr 時退回樣式的 numPr。</summary>
        private static int? ListLevel(ParagraphProperties? properties, Style? style)
        {
            var numbering = properties?.NumberingProperties;
            if (numbering?.NumberingId?.Val?.Value is null)
                numbering = style?.StyleParagraphProperties?.NumberingProperties;

            var numId = numbering?.NumberingId?.Val?.Value;
            if (numbering is null || numId is null or 0)
                return null;

            var level = numbering.NumberingLevelReference?.Val?.Value ?? 0;
            return Math.Clamp(level, 0, MaxListLevel);
        }

        /// <summary>段落的純文字：只取 t、tab（一個空白）、br（換行）；超連結與插入的修訂照常取，刪除的修訂、sym、圖形一律不取。</summary>
        private static string ParagraphText(OpenXmlElement paragraph)
        {
            var builder = new StringBuilder();
            AppendInline(paragraph, builder);
            return builder.ToString().Trim();
        }

        private static void AppendInline(OpenXmlElement parent, StringBuilder builder)
        {
            foreach (var child in parent.ChildElements)
            {
                if (Is(child, "t"))
                    builder.Append(child.InnerText);
                else if (Is(child, "tab") || Is(child, "ptab"))
                    builder.Append(' ');
                else if (Is(child, "br") || Is(child, "cr"))
                    builder.Append('\n');
                else if (IsInlineContainer(child))
                    AppendInline(child, builder);
            }
        }

        /// <summary>會包住文字的行內容器（run、超連結、插入的修訂、欄位等）。刪除的修訂（del、moveFrom）不在內。</summary>
        private static bool IsInlineContainer(OpenXmlElement element) =>
            Is(element, "r") || Is(element, "hyperlink") || Is(element, "ins") || Is(element, "moveTo")
            || Is(element, "fldSimple") || Is(element, "smartTag") || Is(element, "customXml");

        /// <summary>檢查元素是否為 WordprocessingML 主命名空間下指定本地名稱的元素（不論是否被 SDK 讀成型別）。</summary>
        private static bool Is(OpenXmlElement element, string localName) =>
            element.LocalName == localName && element.NamespaceUri == WordNamespace;

        private static bool IsInsideFallback(OpenXmlElement element)
        {
            for (var parent = element.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent.LocalName == FallbackElement)
                    return true;
            }

            return false;
        }
    }
}
