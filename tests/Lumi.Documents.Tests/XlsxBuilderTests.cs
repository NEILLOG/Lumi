#nullable disable   // OpenXml SDK 的導覽屬性標成可為 null；測試直接取值，失敗時以 NullReferenceException 呈現
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Lumi.Documents.Tests.Samples;

namespace Lumi.Documents.Tests;

public class XlsxBuilderTests
{
    [Fact]
    public void Generated_file_reopens_with_the_openxml_sdk()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        using var doc = SpreadsheetDocument.Open(path, isEditable: false);

        Assert.NotNull(doc.WorkbookPart);
    }

    [Fact]
    public void Workbook_has_two_sheets_and_the_second_is_hidden()
    {
        using var stream = new MemoryStream(XlsxBuilder.CreateBytes());
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var sheets = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().ToList();

        Assert.Equal(2, sheets.Count);
        Assert.Equal(XlsxBuilder.VisibleSheet, sheets[0].Name?.Value);
        Assert.Null(sheets[0].State?.Value);
        Assert.Equal(XlsxBuilder.HiddenSheet, sheets[1].Name?.Value);
        Assert.Equal(SheetStateValues.Hidden, sheets[1].State?.Value);
    }

    [Fact]
    public void Date_cell_is_a_serial_number_with_a_date_format()
    {
        using var stream = new MemoryStream(XlsxBuilder.CreateBytes());
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var cell = FindCell(doc, "C2");

        Assert.Equal("46318", cell.CellValue!.Text);   // 2026-10-23 的 Excel 序號
        var format = doc.WorkbookPart!.WorkbookStylesPart!.Stylesheet.CellFormats!.Elements<CellFormat>().ElementAt((int)cell.StyleIndex!.Value);
        var numberFormatId = format.NumberFormatId!.Value;
        var code = doc.WorkbookPart.WorkbookStylesPart.Stylesheet.NumberingFormats!.Elements<NumberingFormat>()
            .Single(n => n.NumberFormatId!.Value == numberFormatId).FormatCode!.Value;
        Assert.Equal("yyyy-mm-dd", code);
    }

    [Fact]
    public void Formula_cell_keeps_its_cached_value()
    {
        using var stream = new MemoryStream(XlsxBuilder.CreateBytes());
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var cell = FindCell(doc, "E2");

        Assert.Equal("B2*2", cell.CellFormula!.Text);
        Assert.Equal(XlsxBuilder.FormulaCachedValue, cell.CellValue!.Text);
    }

    [Fact]
    public void Merged_range_is_recorded_on_the_visible_sheet()
    {
        using var stream = new MemoryStream(XlsxBuilder.CreateBytes());
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var sheet = (Sheet)doc.WorkbookPart!.Workbook.Sheets!.First();
        var worksheet = ((WorksheetPart)doc.WorkbookPart.GetPartById(sheet.Id!)).Worksheet;

        Assert.Equal("A5:C5", worksheet.Elements<MergeCells>().Single().Elements<MergeCell>().Single().Reference!.Value);
    }

    [Fact]
    public void Chinese_text_is_stored_in_the_shared_string_table()
    {
        using var stream = new MemoryStream(XlsxBuilder.CreateBytes());
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var strings = doc.WorkbookPart!.SharedStringTablePart!.SharedStringTable.Elements<SharedStringItem>()
            .Select(s => s.InnerText).ToList();

        Assert.Contains(XlsxBuilder.MergedTitle, strings);
        Assert.Contains("香蕉 😀", strings);
    }

    [Fact]
    public void Hidden_sheet_has_its_own_content()
    {
        using var stream = new MemoryStream(XlsxBuilder.CreateBytes());
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var hidden = (Sheet)doc.WorkbookPart!.Workbook.Sheets!.ElementAt(1);
        var worksheet = ((WorksheetPart)doc.WorkbookPart.GetPartById(hidden.Id!)).Worksheet;

        var cell = worksheet.Descendants<Cell>().Single(c => c.CellReference?.Value == "A1");
        var shared = doc.WorkbookPart.SharedStringTablePart!.SharedStringTable.Elements<SharedStringItem>().ElementAt(int.Parse(cell.CellValue!.Text));
        Assert.Equal(XlsxBuilder.HiddenText, shared.InnerText);
    }

    private static Cell FindCell(SpreadsheetDocument doc, string reference)
    {
        var sheet = (Sheet)doc.WorkbookPart!.Workbook.Sheets!.First();
        var worksheet = ((WorksheetPart)doc.WorkbookPart.GetPartById(sheet.Id!)).Worksheet;
        return worksheet.Descendants<Cell>().Single(c => c.CellReference?.Value == reference);
    }
}
