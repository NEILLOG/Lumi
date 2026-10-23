using System.Globalization;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;

namespace Lumi.Documents;

/// <summary>
/// 用 XmlReader 串流讀取 Excel 的部件（M10 C4）。工作表只讀到需要的列為止，不載成型別樹；共用字串與樣式各讀一次。
/// </summary>
internal static class ExcelXmlParts
{
    /// <summary>工作表的使用範圍：先讀 <c>dimension</c>（只看到 sheetData 之前）；沒有時掃過所有儲存格位址。沒有任何儲存格回 null。</summary>
    public static ExcelRange? GetUsedRange(WorksheetPart? part)
    {
        if (part is null)
            return null;

        var dimension = ReadDimension(part);
        return dimension ?? ScanUsedRange(part);
    }

    /// <summary>
    /// 逐列讀出 <paramref name="window"/> 內的儲存格（欄為 <paramref name="window"/>.Left 起算，陣列長度 = 欄數）。
    /// 讀到 window.Bottom 那一列就停止；每列交給 <paramref name="onRow"/>（列號與已還原的文字），陣列只在呼叫期間有效。
    /// </summary>
    public static void ReadWindow(WorksheetPart part, ExcelRange window, ExcelWorkbookContext context, CancellationToken ct, Action<int, string[]> onRow)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, OpenXmlSafety.CreateXmlReaderSettings());
        var values = new string[window.Right - window.Left + 1];
        var rowNumber = 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                continue;

            ct.ThrowIfCancellationRequested();
            rowNumber = ParseIndex(reader.GetAttribute("r")) ?? rowNumber + 1;
            if (rowNumber > window.Bottom)
                break;

            var wanted = rowNumber >= window.Top;
            Array.Clear(values);
            if (!reader.IsEmptyElement)
                ReadRow(reader, window, context, values, wanted);
            if (wanted)
                onRow(rowNumber, values);
        }
    }

    private static ExcelRange? ReadDimension(WorksheetPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, OpenXmlSafety.CreateXmlReaderSettings());
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (reader.LocalName == "dimension")
            {
                return ExcelAddress.TryParseRange(reader.GetAttribute("ref") ?? "", out var range) && range.Kind == ExcelRangeKind.Cells
                    ? range
                    : null;
            }
            if (reader.LocalName == "sheetData")
                return null;
        }
        return null;
    }

    private static ExcelRange? ScanUsedRange(WorksheetPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, OpenXmlSafety.CreateXmlReaderSettings());
        var top = int.MaxValue;
        var left = int.MaxValue;
        var bottom = 0;
        var right = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c")
                continue;
            if (!ExcelAddress.TryParseCell(reader.GetAttribute("r") ?? "", out var row, out var column))
                continue;
            top = Math.Min(top, row);
            bottom = Math.Max(bottom, row);
            left = Math.Min(left, column);
            right = Math.Max(right, column);
        }
        return bottom == 0 ? null : new ExcelRange(ExcelRangeKind.Cells, top, left, bottom, right);
    }

    /// <summary>讀一列的所有儲存格（呼叫時 reader 在 row 開始標籤上，結束時在 row 結束標籤上）。</summary>
    private static void ReadRow(XmlReader reader, ExcelRange window, ExcelWorkbookContext context, string[] values, bool wanted)
    {
        var rowDepth = reader.Depth;
        var previousColumn = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == rowDepth)
                return;
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c" || reader.Depth != rowDepth + 1)
                continue;

            var column = ExcelAddress.TryParseCell(reader.GetAttribute("r") ?? "", out _, out var parsed) ? parsed : previousColumn + 1;
            previousColumn = column;
            var index = wanted && column >= window.Left && column <= window.Right ? column - window.Left : -1;
            ReadCell(reader, context, values, index);
        }
    }

    /// <summary>讀一個儲存格並把還原後的文字放進 <paramref name="values"/>[index]（index 小於 0 表示不在視窗內，只消耗內容）。</summary>
    private static void ReadCell(XmlReader reader, ExcelWorkbookContext context, string[] values, int index)
    {
        var type = reader.GetAttribute("t");
        var styleIndex = ParseIndex(reader.GetAttribute("s")) ?? 0;
        string? value = null;
        string? inline = null;

        if (!reader.IsEmptyElement)
        {
            var cellDepth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellDepth)
                    break;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != cellDepth + 1)
                    continue;

                if (reader.LocalName == "v")
                    value = ReadText(reader);
                else if (reader.LocalName == "is")
                    inline = ReadRichText(reader);
            }
        }

        if (index >= 0)
            values[index] = context.Display(type, value, inline, styleIndex);
    }

    /// <summary>讀出元素內所有文字（含 CDATA 與空白），結束時 reader 停在該元素的結束標籤上。</summary>
    internal static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return "";
        var depth = reader.Depth;
        var builder = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                builder.Append(reader.Value);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 讀富文字（<c>si</c> 或 <c>is</c>）：串接所有 <c>t</c>，略過注音（<c>rPh</c>）。結束時 reader 停在該元素的結束標籤上。
    /// </summary>
    internal static string ReadRichText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return "";
        var depth = reader.Depth;
        var phoneticDepth = -1;
        var builder = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "rPh" && reader.Depth == phoneticDepth)
                    phoneticDepth = -1;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (reader.LocalName == "rPh")
                phoneticDepth = reader.Depth;
            else if (reader.LocalName == "t" && phoneticDepth < 0)
                builder.Append(ReadText(reader));
        }
        return builder.ToString();
    }

    /// <summary>讀共用字串表；總字元數超過 <see cref="ExcelDocumentReader.MaxSharedStringChars"/> 丟 <see cref="DocumentRejectedException"/>。</summary>
    internal static IReadOnlyList<string> ReadSharedStrings(SharedStringTablePart? part)
    {
        var list = new List<string>();
        if (part is null)
            return list;

        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, OpenXmlSafety.CreateXmlReaderSettings());
        long total = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
                continue;

            var text = ReadRichText(reader);
            total += text.Length;
            if (total > ExcelDocumentReader.MaxSharedStringChars)
                throw new DocumentRejectedException(ExcelDocumentReader.SharedStringLimitMessage);
            list.Add(text);
        }
        return list;
    }

    /// <summary>讀樣式：自訂數字格式（numFmts）與 cellXfs 的每個樣式對應的數字種類，依 xf 的順序排列。</summary>
    internal static IReadOnlyList<ExcelNumberStyle> ReadCellStyles(WorkbookStylesPart? part)
    {
        var customFormats = new Dictionary<int, string>();
        var formatIds = new List<int>();
        if (part is null)
            return [];

        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, OpenXmlSafety.CreateXmlReaderSettings());
        var numFmtsDepth = -1;
        var cellXfsDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "numFmts" && reader.Depth == numFmtsDepth)
                    numFmtsDepth = -1;
                else if (reader.LocalName == "cellXfs" && reader.Depth == cellXfsDepth)
                    cellXfsDepth = -1;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.LocalName)
            {
                case "numFmts" when !reader.IsEmptyElement:
                    numFmtsDepth = reader.Depth;
                    break;
                case "numFmt" when numFmtsDepth >= 0 && reader.Depth == numFmtsDepth + 1:
                    var id = ParseIndex(reader.GetAttribute("numFmtId"));
                    var code = reader.GetAttribute("formatCode");
                    if (id is not null && code is not null)
                        customFormats[id.Value] = code;
                    break;
                case "cellXfs" when !reader.IsEmptyElement:
                    cellXfsDepth = reader.Depth;
                    break;
                case "xf" when cellXfsDepth >= 0 && reader.Depth == cellXfsDepth + 1:
                    formatIds.Add(ParseIndex(reader.GetAttribute("numFmtId")) ?? 0);
                    break;
            }
        }

        return formatIds.Select(id => ExcelNumberFormat.ForFormatId(id, customFormats)).ToList();
    }

    /// <summary>非負整數（列號、樣式索引等）；不是數字回 null。</summary>
    private static int? ParseIndex(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>一本活頁簿的讀取上下文：共用字串與樣式在第一次需要時才讀，並依格式還原儲存格文字。</summary>
internal sealed class ExcelWorkbookContext
{
    private readonly Lazy<IReadOnlyList<string>> _sharedStrings;
    private readonly Lazy<IReadOnlyList<ExcelNumberStyle>> _styles;

    public ExcelWorkbookContext(WorkbookPart workbook, bool date1904)
    {
        Date1904 = date1904;
        _sharedStrings = new Lazy<IReadOnlyList<string>>(() => ExcelXmlParts.ReadSharedStrings(workbook.SharedStringTablePart));
        _styles = new Lazy<IReadOnlyList<ExcelNumberStyle>>(() => ExcelXmlParts.ReadCellStyles(workbook.WorkbookStylesPart));
    }

    public bool Date1904 { get; }

    /// <summary>把一個儲存格的原始值（型別屬性 t、<c>v</c>、內嵌字串與樣式索引）還原成要顯示的文字。</summary>
    public string Display(string? type, string? value, string? inlineText, int styleIndex)
    {
        switch (type)
        {
            case "s":
                var shared = _sharedStrings.Value;
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count
                    ? shared[index]
                    : "";
            case "inlineStr":
                return inlineText ?? "";
            case "str":
                return value ?? "";
            case "b":
                return value is null or "" ? "" : value == "1" ? "TRUE" : "FALSE";
            case "e":
                return value ?? "";
            default:
                if (string.IsNullOrEmpty(value))
                    return "";
                if (type == "d")
                    return value;
                var styles = _styles.Value;
                var style = styleIndex >= 0 && styleIndex < styles.Count ? styles[styleIndex] : ExcelNumberStyle.Plain;
                return ExcelNumberFormat.Format(value, style, Date1904);
        }
    }
}
