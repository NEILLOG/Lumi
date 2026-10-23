using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Lumi.Documents.Tests.Samples;

/// <summary>
/// 產生 .xlsx 樣本：兩張工作表（一張隱藏）；文字、數字、日期（yyyy-mm-dd）、公式（含快取值）、合併儲存格、中文與 emoji。
/// </summary>
public static class XlsxBuilder
{
    public const string VisibleSheet = "專案進度";
    public const string HiddenSheet = "隱藏資料";
    public const string MergedTitle = "合併儲存格標題";
    public const string HiddenText = "隱藏工作表的內容";

    /// <summary>可見工作表的日期（儲存格存的是序號，格式是 yyyy-mm-dd）。</summary>
    public static readonly DateOnly SampleDate = new(2026, 10, 23);

    /// <summary>公式 <c>B2*2</c> 的快取值（B2 = 3）。</summary>
    public const string FormulaCachedValue = "6";

    public static byte[] CreateBytes()
    {
        using var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbook = doc.AddWorkbookPart();
            workbook.Workbook = new Workbook();

            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = BuildStylesheet();

            var strings = new SharedStringBuilder(workbook.AddNewPart<SharedStringTablePart>());

            var visible = workbook.AddNewPart<WorksheetPart>();
            visible.Worksheet = new Worksheet(
                new SheetData(
                    Row(1,
                        Text("A1", "項目", strings),
                        Text("B1", "數量", strings),
                        Text("C1", "日期", strings),
                        Text("D1", "金額", strings),
                        Text("E1", "小計（公式）", strings)),
                    Row(2,
                        Text("A2", "蘋果", strings),
                        Number("B2", "3"),
                        Date("C2", SampleDate),
                        Number("D2", "1234.5", StyleIndexes.Money),
                        Formula("E2", "B2*2", FormulaCachedValue)),
                    Row(3,
                        Text("A3", "香蕉 😀", strings),
                        Number("B3", "10"),
                        Date("C3", new DateOnly(2026, 1, 2)),
                        Number("D3", "99"),
                        Formula("E3", "B3*2", "20")),
                    Row(5,
                        Text("A5", MergedTitle, strings))),
                new MergeCells(new MergeCell { Reference = "A5:C5" }));

            var hidden = workbook.AddNewPart<WorksheetPart>();
            hidden.Worksheet = new Worksheet(new SheetData(
                Row(1, Text("A1", HiddenText, strings))));

            workbook.Workbook.Append(new Sheets(
                new Sheet { Name = VisibleSheet, SheetId = 1, Id = workbook.GetIdOfPart(visible) },
                new Sheet { Name = HiddenSheet, SheetId = 2, State = SheetStateValues.Hidden, Id = workbook.GetIdOfPart(hidden) }));
        }

        return stream.ToArray();
    }

    /// <summary>寫入 <paramref name="directory"/>，回傳完整路徑。</summary>
    public static string Create(string directory, string fileName = "sample.xlsx") =>
        SampleFiles.Write(directory, fileName, CreateBytes());

    /// <summary>樣式索引：0 一般、1 日期（yyyy-mm-dd）、2 金額（#,##0.00）。</summary>
    private static class StyleIndexes
    {
        public const uint Plain = 0;
        public const uint Date = 1;
        public const uint Money = 2;
    }

    private static Stylesheet BuildStylesheet() => new(
        new NumberingFormats(new NumberingFormat { NumberFormatId = 164U, FormatCode = "yyyy-mm-dd" }),
        new Fonts(new Font()),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
        new Borders(new Border()),
        new CellStyleFormats(new CellFormat()),
        new CellFormats(
            new CellFormat(),
            new CellFormat { NumberFormatId = 164U, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 4U, ApplyNumberFormat = true }));

    private static Row Row(uint index, params Cell[] cells) => new(cells) { RowIndex = index };

    private static Cell Text(string reference, string text, SharedStringBuilder strings) => new(
        new CellValue(strings.IndexOf(text).ToString(CultureInfo.InvariantCulture)))
    {
        CellReference = reference,
        DataType = CellValues.SharedString,
    };

    private static Cell Number(string reference, string value, uint style = StyleIndexes.Plain) => new(
        new CellValue(value))
    {
        CellReference = reference,
        StyleIndex = style,
    };

    private static Cell Date(string reference, DateOnly date)
    {
        // Excel 的日期是「1899-12-30 起算的天數」（1900 年閏年錯誤之後的日期都適用）
        var serial = date.ToDateTime(TimeOnly.MinValue).Subtract(new DateTime(1899, 12, 30)).Days;
        return Number(reference, serial.ToString(CultureInfo.InvariantCulture), StyleIndexes.Date);
    }

    private static Cell Formula(string reference, string formula, string cachedValue) => new(
        new CellFormula(formula),
        new CellValue(cachedValue))
    {
        CellReference = reference,
    };

    /// <summary>共用字串表：同一個字串只存一次。</summary>
    private sealed class SharedStringBuilder
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly SharedStringTable _table = new();

        public SharedStringBuilder(SharedStringTablePart part)
        {
            part.SharedStringTable = _table;   // 只設定一次；之後只往 _table 追加
        }

        public int IndexOf(string text)
        {
            if (_index.TryGetValue(text, out var existing))
                return existing;

            _table.Append(new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text(text)));
            _index[text] = (int)_table.ChildElements.Count - 1;
            return _index[text];
        }
    }
}
