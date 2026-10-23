#nullable disable   // OpenXml 的導覽屬性標成可為 null；測試直接取值，失敗時以 NullReferenceException 呈現
using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Lumi.Core.Documents;
using Lumi.Documents.Tests.Samples;
using Microsoft.Extensions.Logging.Abstractions;

using CoreDocumentFormat = Lumi.Core.Documents.DocumentFormat;

namespace Lumi.Documents.Tests;

public class ExcelDocumentReaderTests
{
    private const int GeneralStyle = 0;
    private const int BuiltinDateStyle = 1;
    private const int CustomDateStyle = 2;
    private const int ElapsedStyle = 3;
    private const int PercentStyle = 5;
    private const int TimeStyle = 6;

    [Fact]
    public void Supports_xlsx_and_xlsm_only()
    {
        var reader = new ExcelDocumentReader();

        Assert.True(reader.Supports(".xlsx"));
        Assert.True(reader.Supports(".XLSM"));
        Assert.False(reader.Supports(".xls"));
        Assert.False(reader.Supports(".docx"));
    }

    [Fact]
    public async Task Sample_overview_lists_visible_sheets_and_counts_the_hidden_ones()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path);

        Assert.False(result.IsError);
        Assert.Equal(CoreDocumentFormat.Excel, result.Format);
        Assert.StartsWith("[概覽] 工作表 2 張（可見 1）\n- 專案進度：使用範圍 A1:E5（5 列 × 5 欄）\n另有 1 張隱藏的工作表（未顯示）\n---\n", result.Content);
        Assert.DoesNotContain(XlsxBuilder.HiddenText, result.Content);
        Assert.DoesNotContain(XlsxBuilder.HiddenSheet, result.Content);
    }

    [Fact]
    public async Task Sample_reads_dates_formula_cache_merged_cells_chinese_and_emoji()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path);

        Assert.Contains("## 專案進度　A1:E5\n", result.Content);
        Assert.Contains("| 列 | A | B | C | D | E |\n|---|---|---|---|---|---|\n", result.Content);
        Assert.Contains("| 1 | 項目 | 數量 | 日期 | 金額 | 小計（公式） |", result.Content);
        Assert.Contains("| 2 | 蘋果 | 3 | 2026-10-23 | 1234.5 | 6 |", result.Content);
        Assert.Contains("| 3 | 香蕉 😀 | 10 | 2026-01-02 | 99 | 20 |", result.Content);
        Assert.Contains($"| 5 | {XlsxBuilder.MergedTitle} |  |  |  |  |", result.Content);
        Assert.DoesNotContain("| 4 |", result.Content);   // 完全空白的列略過
        Assert.DoesNotContain("B2*2", result.Content);      // 不輸出公式文字
    }

    [Fact]
    public async Task Summary_reports_the_sheet_name_and_the_number_of_rows_shown()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path);

        Assert.Equal($"Excel · {XlsxBuilder.VisibleSheet} · 4 列", result.Summary);
    }

    [Fact]
    public async Task Default_sheet_is_the_first_visible_one_even_when_a_hidden_sheet_comes_first()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("隱藏", Rows(Row(1, Num("A1", "1"))), state: "hidden");
        book.AddSheet("可見", Rows(Row(1, Num("A1", "2"))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("## 可見　A1\n", result.Content);
        Assert.Contains("| 1 | 2 |", result.Content);
    }

    [Fact]
    public async Task Sheet_name_is_matched_case_insensitively()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("Data", Rows(Row(1, Num("A1", "7"))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path, sheet: "  data ");

        Assert.False(result.IsError);
        Assert.Contains("## Data　A1", result.Content);
    }

    [Fact]
    public async Task Unknown_sheet_error_lists_the_visible_sheet_names()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, sheet: "不存在");

        Assert.True(result.IsError);
        Assert.Equal($"找不到工作表「不存在」。可見的工作表：{XlsxBuilder.VisibleSheet}。", result.Content);
        Assert.DoesNotContain(folder.Path, result.Content);
    }

    [Fact]
    public async Task Hidden_sheet_cannot_be_selected_by_name()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, sheet: XlsxBuilder.HiddenSheet);

        Assert.True(result.IsError);
        Assert.Equal($"工作表「{XlsxBuilder.HiddenSheet}」是隱藏的，無法讀取。可見的工作表：{XlsxBuilder.VisibleSheet}。", result.Content);
    }

    [Fact]
    public async Task Workbook_without_visible_sheets_is_an_error()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("只有隱藏", Rows(Row(1, Num("A1", "1"))), state: "hidden");
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.True(result.IsError);
        Assert.Equal(ExcelDocumentReader.NoVisibleSheetMessage, result.Content);
    }

    [Theory]
    [InlineData("A1:B2", "## 專案進度　A1:B2\n", "| 2 | 蘋果 | 3 |")]
    [InlineData("b3", "## 專案進度　B3\n", "| 3 | 10 |")]
    [InlineData("A5:C5", "## 專案進度　A5:C5\n", "| 5 | 合併儲存格標題 |  |  |")]
    public async Task Range_limits_the_table_and_is_shown_in_the_heading(string range, string heading, string row)
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, range: range);

        Assert.False(result.IsError);
        Assert.Contains(heading, result.Content);
        Assert.Contains(row, result.Content);
    }

    [Fact]
    public async Task Range_is_normalized_when_its_corners_are_reversed()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, range: "B2:A1");

        Assert.Contains("## 專案進度　A1:B2\n", result.Content);
    }

    [Fact]
    public async Task Column_range_uses_the_rows_of_the_used_range()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, range: "A:B");

        Assert.Contains("## 專案進度　A1:B5\n", result.Content);
        Assert.Contains("| 1 | 項目 | 數量 |", result.Content);
        Assert.DoesNotContain("日期", result.Content);
    }

    [Fact]
    public async Task Row_range_uses_the_columns_of_the_used_range()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, range: "2:3");

        Assert.Contains("## 專案進度　A2:E3\n", result.Content);
        Assert.DoesNotContain("| 1 | 項目", result.Content);
        Assert.Contains("| 3 | 香蕉 😀 | 10 | 2026-01-02 | 99 | 20 |", result.Content);
    }

    [Theory]
    [InlineData("A1:")]
    [InlineData("hello")]
    [InlineData("5")]
    [InlineData("A0")]
    [InlineData("1:2:3")]
    [InlineData("AAAA1:B2")]
    [InlineData("XFE1")]
    [InlineData("A1048577")]
    public async Task Invalid_range_returns_the_usage_message(string range)
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, range: range);

        Assert.True(result.IsError);
        Assert.Equal(ExcelDocumentReader.UsageMessage, result.Content);
    }

    [Fact]
    public async Task Range_outside_the_content_says_there_is_nothing_to_show()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);

        var result = await ReadAsync(path, range: "A100:B110");

        Assert.False(result.IsError);
        Assert.Contains("## 專案進度　A100:B110\n\n（這個範圍沒有內容）", result.Content);
    }

    [Fact]
    public async Task Pipes_and_line_breaks_inside_cells_are_escaped()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1, Str("A1", "a|b\nc\r\nd"))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | a\\|b<br>c<br>d |", result.Content);
    }

    [Fact]
    public async Task Cell_text_longer_than_500_characters_is_cut_with_an_ellipsis()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1, Str("A1", new string('字', 600)))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | " + new string('字', 500) + "… |", result.Content);
    }

    [Fact]
    public async Task Booleans_errors_inline_strings_and_string_formulas_are_shown_as_is()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1,
            Bool("A1", true), Bool("B1", false), Err("C1", "#DIV/0!"),
            Inline("D1", "行內字串"), Formula("E1", "\"甲\"&\"乙\"", "甲乙", "str"))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | TRUE | FALSE | #DIV/0! | 行內字串 | 甲乙 |", result.Content);
    }

    [Fact]
    public async Task Formula_without_a_cached_value_is_empty()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1, Formula("A1", "SUM(1,2)", null), Num("B1", "5"))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 |  | 5 |", result.Content);
    }

    [Fact]
    public async Task Phonetic_guide_text_is_not_part_of_the_shared_string()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        var index = book.AddRawString("<si><r><t>東京</t></r><rPh sb=\"0\" eb=\"2\"><t>とうきょう</t></rPh></si>");
        book.AddSheet("表", Rows(Row(1, $"<c r=\"A1\" t=\"s\"><v>{index}</v></c>")));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | 東京 |", result.Content);
        Assert.DoesNotContain("とうきょう", result.Content);
    }

    [Fact]
    public async Task Numbers_drop_floating_point_tails_and_point_zero()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1, Num("A1", "0.30000000000000004"), Num("B1", "1234"), Num("C1", "-2.5"), Num("D1", "0.125", PercentStyle))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | 0.3 | 1234 | -2.5 | 12.5% |", result.Content);
    }

    [Fact]
    public async Task Blank_rows_are_skipped_but_keep_their_real_row_numbers()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1, Num("A1", "1")), Row(2, Num("B2", "")), Row(3, Num("A3", "3"))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | 1 |", result.Content);
        Assert.Contains("| 3 | 3 |", result.Content);
        Assert.DoesNotContain("| 2 |", result.Content);
        Assert.Equal("Excel · 表 · 2 列", result.Summary);
    }

    [Fact]
    public async Task Sheet_with_no_cells_says_so()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("空的", Rows(), dimension: null);
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.False(result.IsError);
        Assert.Contains("[概覽] 工作表 1 張（可見 1）\n- 空的：使用範圍 無（0 列 × 0 欄）\n---\n## 空的　（沒有內容）", result.Content);
        Assert.Equal("Excel · 空的 · 0 列", result.Summary);
    }

    [Fact]
    public async Task Used_range_is_worked_out_from_the_cells_when_the_dimension_is_missing()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1, Num("A1", "1")), Row(3, Num("C3", "9"))), dimension: null);
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("- 表：使用範圍 A1:C3（3 列 × 3 欄）", result.Content);
        Assert.Contains("## 表　A1:C3", result.Content);
        Assert.Contains("| 3 |  |  | 9 |", result.Content);
    }

    [Fact]
    public async Task Excel_1900_leap_year_bug_and_1904_system_dates_are_restored()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook { Date1904 = true };
        book.AddSheet("表", Rows(Row(1, Num("A1", "0", CustomDateStyle), Num("B1", "1", CustomDateStyle), Num("C1", "46318", CustomDateStyle))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | 1904-01-01 | 1904-01-02 | 2030-10-24 |", result.Content);
    }

    [Theory]
    [InlineData(1, "1900-01-01")]
    [InlineData(59, "1900-02-28")]
    [InlineData(60, "1900-02-29")]
    [InlineData(61, "1900-03-01")]
    [InlineData(46318, "2026-10-23")]
    public void Serial_numbers_around_the_1900_leap_year_bug_are_converted(double serial, string expected)
    {
        var style = new ExcelNumberStyle(ExcelNumberKind.Date);

        Assert.Equal(expected, ExcelNumberFormat.Format(serial.ToString(System.Globalization.CultureInfo.InvariantCulture), style, date1904: false));
    }

    [Fact]
    public void Dates_with_a_time_part_show_the_time_and_time_only_formats_show_just_the_time()
    {
        var date = ExcelNumberFormat.Format("46318.5", new ExcelNumberStyle(ExcelNumberKind.Date), false);
        var time = ExcelNumberFormat.Format("0.75", new ExcelNumberStyle(ExcelNumberKind.Time), false);
        var elapsed = ExcelNumberFormat.Format("1.5", new ExcelNumberStyle(ExcelNumberKind.Elapsed), false);

        Assert.Equal("2026-10-23 12:00:00", date);
        Assert.Equal("18:00:00", time);
        Assert.Equal("36:00", elapsed);
    }

    [Theory]
    [InlineData(14, "Date")]
    [InlineData(22, "Date")]
    [InlineData(20, "Time")]
    [InlineData(46, "Elapsed")]
    [InlineData(9, "Percent")]
    [InlineData(10, "Percent")]
    [InlineData(4, "Plain")]
    [InlineData(0, "Plain")]
    public void Builtin_format_ids_are_classified(int id, string expected)
    {
        Assert.Equal(expected, ExcelNumberFormat.ForFormatId(id, new Dictionary<int, string>()).Kind.ToString());
    }

    [Theory]
    [InlineData("yyyy/m/d", "Date")]
    [InlineData("yyyy-mm-dd", "Date")]
    [InlineData("m", "Date")]
    [InlineData("[$-409]yyyy/m/d", "Date")]
    [InlineData("yyyy\"年\"m\"月\"d\"日\"", "Date")]
    [InlineData("[h]:mm", "Elapsed")]
    [InlineData("h:mm", "Time")]
    [InlineData("mm:ss", "Time")]
    [InlineData("h:mm AM/PM", "Time")]
    [InlineData("#,##0.00", "Plain")]
    [InlineData("\"第\"0\"期\"", "Plain")]
    [InlineData("\"yyyy\" 0.00", "Plain")]
    [InlineData("[Red]0.00", "Plain")]
    [InlineData("0.00\\d", "Plain")]
    [InlineData("0.0%", "Percent")]
    [InlineData("General", "Plain")]
    public void Custom_format_codes_are_classified(string code, string expected)
    {
        Assert.Equal(expected, ExcelNumberFormat.Classify(code).Kind.ToString());
    }

    [Fact]
    public async Task Custom_date_format_from_a_file_is_restored()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(Row(1, Num("A1", "46318", CustomDateStyle), Num("B1", "1.5", ElapsedStyle), Num("C1", "0.5", TimeStyle), Num("D1", "1234.5678", GeneralStyle))));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("| 1 | 2026-10-23 | 36:00 | 12:00:00 | 1234.5678 |", result.Content);
    }

    [Fact]
    public async Task Wide_sheet_shows_30_columns_and_says_how_many_are_left()
    {
        using var folder = new TempFolder();
        var cells = Enumerable.Range(1, 200).Select(c => Num(ExcelAddress.ColumnName(c) + "1", c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var book = new TestWorkbook();
        book.AddSheet("寬表", Rows(RowX(1, cells)));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("## 寬表　A1:AD1\n", result.Content);
        Assert.Contains("| 列 | A | B |", result.Content);
        Assert.Contains("| AD |\n", result.Content);
        Assert.DoesNotContain("| AE |", result.Content);
        Assert.Contains("（還有 170 欄未顯示，請用 range 指定範圍，例如 AE1:BH1）", result.Content);
    }

    [Fact]
    public async Task Long_sheet_shows_200_rows_and_says_how_many_are_left()
    {
        using var folder = new TempFolder();
        var rows = Enumerable.Range(1, 1380).Select(r => Row(r, Num("A" + r, r.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var book = new TestWorkbook();
        book.AddSheet("長表", Rows(rows.ToArray()), dimension: "A1:A1380");
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path);

        Assert.Contains("- 長表：使用範圍 A1:A1380（1,380 列 × 1 欄）", result.Content);
        Assert.Contains("| 200 | 200 |", result.Content);
        Assert.DoesNotContain("| 201 |", result.Content);
        Assert.Contains("（還有 1,180 列未顯示，請用 range 指定範圍，例如 A201:A400）", result.Content);
        Assert.Equal("Excel · 長表 · 200 列", result.Summary);
    }

    [Fact]
    public async Task Explicit_range_past_the_limit_is_capped_and_reports_the_rest()
    {
        using var folder = new TempFolder();
        var rows = Enumerable.Range(1, 500).Select(r => Row(r, Num("A" + r, r.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var book = new TestWorkbook();
        book.AddSheet("表", Rows(rows.ToArray()));
        var path = folder.Write("a.xlsx", book.Build());

        var result = await ReadAsync(path, range: "A1:A500");

        Assert.Contains("## 表　A1:A200\n", result.Content);
        Assert.Contains("（還有 300 列未顯示，請用 range 指定範圍，例如 A201:A400）", result.Content);
        var second = await ReadAsync(path, range: "A201:A400");
        Assert.Contains("| 201 | 201 |", second.Content);
    }

    [Fact]
    public async Task Huge_sheet_is_read_only_as_far_as_the_window_and_reports_the_rest()
    {
        using var folder = new TempFolder();
        var path = CreateLargeWorkbook(folder.Path, rows: 200_000);
        var watch = Stopwatch.StartNew();

        var result = await ReadAsync(path);

        watch.Stop();
        Assert.False(result.IsError);
        Assert.Contains("- 大表：使用範圍 A1:B200000（200,000 列 × 2 欄）", result.Content);
        Assert.Contains("| 200 | 200 | 400 |", result.Content);
        Assert.DoesNotContain("| 201 |", result.Content);
        Assert.Contains("（還有 199,800 列未顯示，請用 range 指定範圍，例如 A201:B400）", result.Content);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"讀取花了 {watch.Elapsed.TotalSeconds:F1} 秒");
    }

    [Fact]
    public async Task Huge_sheet_range_near_the_bottom_is_read_without_reading_from_the_top()
    {
        using var folder = new TempFolder();
        var path = CreateLargeWorkbook(folder.Path, rows: 200_000);

        var result = await ReadAsync(path, range: "199998:200000");

        Assert.Contains("| 199998 | 199998 | 399996 |", result.Content);
        Assert.Contains("| 200000 | 200000 | 400000 |", result.Content);
        Assert.DoesNotContain("還有", result.Content);
    }

    [Fact]
    public async Task Shared_string_table_over_the_limit_is_rejected()
    {
        using var folder = new TempFolder();
        var book = new TestWorkbook();
        var index = book.AddRawString("<si><t>" + new string('a', (int)ExcelDocumentReader.MaxSharedStringChars + 1) + "</t></si>");
        book.AddSheet("表", Rows(Row(1, $"<c r=\"A1\" t=\"s\"><v>{index}</v></c>")));
        var path = folder.Write("a.xlsx", book.Build());

        await Assert.ThrowsAsync<DocumentRejectedException>(async () => await ReadAsync(path));
    }

    [Fact]
    public async Task Cancellation_stops_the_read()
    {
        using var folder = new TempFolder();
        var path = XlsxBuilder.Create(folder.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new ExcelDocumentReader().ReadAsync(new DocumentReadRequest(path), cts.Token));
    }

    [Fact]
    public async Task Xlsm_files_are_read_the_same_way()
    {
        using var folder = new TempFolder();
        var path = folder.Write("macro.xlsm", XlsxBuilder.CreateBytes());

        var result = await ReadAsync(path);

        Assert.False(result.IsError);
        Assert.Contains("| 2 | 蘋果 | 3 |", result.Content);
    }

    [Fact]
    public async Task Garbage_and_empty_files_give_the_fixed_failure_message_through_the_reader()
    {
        using var folder = new TempFolder();
        var garbage = folder.Write("garbage.xlsx", BrokenFiles.Garbage());
        var empty = folder.Write("empty.xlsx", BrokenFiles.Empty());
        var reader = new DocumentReader([new ExcelDocumentReader()], new DocumentOptions(), NullLogger<DocumentReader>.Instance);

        var fromGarbage = await reader.ReadAsync(new DocumentReadRequest(garbage), CancellationToken.None);
        var fromEmpty = await reader.ReadAsync(new DocumentReadRequest(empty), CancellationToken.None);

        Assert.True(fromGarbage.IsError);
        Assert.Equal(DocumentReader.FailedMessage, fromGarbage.Content);
        Assert.True(fromEmpty.IsError);
        Assert.Equal(DocumentReader.FailedMessage, fromEmpty.Content);
    }

    [Fact]
    public async Task Truncated_workbook_gives_the_fixed_failure_message_through_the_reader()
    {
        using var folder = new TempFolder();
        var full = XlsxBuilder.CreateBytes();
        var path = folder.Write("cut.xlsx", full[..(full.Length / 2)]);
        var reader = new DocumentReader([new ExcelDocumentReader()], new DocumentOptions(), NullLogger<DocumentReader>.Instance);

        var result = await reader.ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.FailedMessage, result.Content);
    }

    // ---- 測試用的 .xlsx 組裝：手寫 XML 放進 zip，可以精確控制 dimension、樣式與字串表 ----

    private static Task<DocumentReadResult> ReadAsync(string path, string sheet = null, string range = null) =>
        new ExcelDocumentReader().ReadAsync(new DocumentReadRequest(path, Sheet: sheet, Range: range), CancellationToken.None);

    private static string Rows(params string[] rows) => string.Concat(rows);

    private static string Row(int number, params string[] cells) => RowX(number, cells);

    private static string RowX(int number, IEnumerable<string> cells) => $"<row r=\"{number}\">{string.Concat(cells)}</row>";

    private static string Num(string reference, string value, int style = 0) =>
        $"<c r=\"{reference}\" s=\"{style}\"><v>{value}</v></c>";

    private static string Str(string reference, string text) => Inline(reference, text);

    private static string Inline(string reference, string text) =>
        $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(text)}</t></is></c>";

    private static string Bool(string reference, bool value) => $"<c r=\"{reference}\" t=\"b\"><v>{(value ? 1 : 0)}</v></c>";

    private static string Err(string reference, string error) => $"<c r=\"{reference}\" t=\"e\"><v>{error}</v></c>";

    private static string Formula(string reference, string formula, string cached, string type = null)
    {
        var typeAttribute = type is null ? "" : $" t=\"{type}\"";
        var value = cached is null ? "" : $"<v>{SecurityElement.Escape(cached)}</v>";
        return $"<c r=\"{reference}\"{typeAttribute}><f>{SecurityElement.Escape(formula)}</f>{value}</c>";
    }

    /// <summary>手寫 .xlsx 的組裝器：每張工作表給一段 sheetData，字串放進共用字串表。</summary>
    private sealed class TestWorkbook
    {
        private readonly List<string> _strings = [];
        private readonly List<string> _rawStrings = [];
        private readonly List<(string Name, string State, string Dimension, string SheetData)> _sheets = [];

        public bool Date1904 { get; set; }

        /// <summary>加入一張工作表；<paramref name="dimension"/> 為 null 時不寫 dimension 元素。</summary>
        public void AddSheet(string name, string sheetData, string state = null, string dimension = null)
        {
            _sheets.Add((name, state, dimension, sheetData));
        }

        /// <summary>加入一個原樣的 &lt;si&gt; 字串，回傳索引。</summary>
        public int AddRawString(string siXml)
        {
            _rawStrings.Add(siXml);
            return _rawStrings.Count - 1;
        }

        public byte[] Build()
        {
            var parts = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = ContentTypes(),
                ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
                ["xl/workbook.xml"] = Workbook(),
                ["xl/_rels/workbook.xml.rels"] = WorkbookRels(),
                ["xl/styles.xml"] = Styles,
                ["xl/sharedStrings.xml"] = SharedStrings(),
            };
            for (var i = 0; i < _sheets.Count; i++)
                parts[$"xl/worksheets/sheet{i + 1}.xml"] = Worksheet(_sheets[i]);

            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in parts)
                {
                    var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(content);
                }
            }
            return stream.ToArray();
        }

        private string Worksheet((string Name, string State, string Dimension, string SheetData) sheet) =>
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + (sheet.Dimension is null ? "" : $"<dimension ref=\"{sheet.Dimension}\"/>")
            + $"<sheetData>{sheet.SheetData}</sheetData></worksheet>";

        private string Workbook()
        {
            var sheets = string.Concat(_sheets.Select((sheet, i) =>
                $"<sheet name=\"{SecurityElement.Escape(sheet.Name)}\" sheetId=\"{i + 1}\"{(sheet.State is null ? "" : $" state=\"{sheet.State}\"")} r:id=\"rId{i + 1}\"/>"));
            var properties = Date1904 ? "<workbookPr date1904=\"1\"/>" : "";
            return "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + properties + $"<sheets>{sheets}</sheets></workbook>";
        }

        private string WorkbookRels()
        {
            var sheets = string.Concat(_sheets.Select((_, i) =>
                $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"));
            var next = _sheets.Count + 1;
            return "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + sheets
                + $"<Relationship Id=\"rId{next}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>"
                + $"<Relationship Id=\"rId{next + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                + "</Relationships>";
        }

        private string ContentTypes()
        {
            var sheets = string.Concat(_sheets.Select((_, i) =>
                $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
            return "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + sheets
                + "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + "</Types>";
        }

        private string SharedStrings()
        {
            var items = _strings.Select(s => $"<si><t xml:space=\"preserve\">{SecurityElement.Escape(s)}</t></si>")
                .Concat(_rawStrings);
            return "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" + string.Concat(items) + "</sst>";
        }

        /// <summary>樣式：0 一般、1 內建日期（14）、2 yyyy/m/d、3 [h]:mm、4 #,##0.00、5 0.00%（10）、6 h:mm（20）、7 "第"0"期"。</summary>
        private static string Styles =>
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<numFmts count=\"3\"><numFmt numFmtId=\"164\" formatCode=\"yyyy/m/d\"/><numFmt numFmtId=\"165\" formatCode=\"[h]:mm\"/><numFmt numFmtId=\"166\" formatCode=\"&quot;第&quot;0&quot;期&quot;\"/></numFmts>"
            + "<fonts count=\"1\"><font/></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>"
            + "<cellXfs count=\"8\">"
            + "<xf numFmtId=\"0\"/><xf numFmtId=\"14\" applyNumberFormat=\"1\"/><xf numFmtId=\"164\" applyNumberFormat=\"1\"/>"
            + "<xf numFmtId=\"165\" applyNumberFormat=\"1\"/><xf numFmtId=\"4\" applyNumberFormat=\"1\"/><xf numFmtId=\"10\" applyNumberFormat=\"1\"/>"
            + "<xf numFmtId=\"20\" applyNumberFormat=\"1\"/><xf numFmtId=\"166\" applyNumberFormat=\"1\"/>"
            + "</cellXfs></styleSheet>";
    }

    /// <summary>用 OpenXml 的串流寫入器產生大工作表（有 dimension）：A 欄是列號，B 欄是列號的兩倍。</summary>
    private static string CreateLargeWorkbook(string directory, int rows)
    {
        var path = Path.Combine(directory, "large.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(worksheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteElement(new SheetDimension { Reference = $"A1:B{rows}" });
                writer.WriteStartElement(new SheetData());
                for (var r = 1; r <= rows; r++)
                {
                    writer.WriteStartElement(new Row { RowIndex = (uint)r });
                    writer.WriteElement(new Cell { CellReference = $"A{r}", CellValue = new CellValue(r.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
                    writer.WriteElement(new Cell { CellReference = $"B{r}", CellValue = new CellValue((2 * r).ToString(System.Globalization.CultureInfo.InvariantCulture)) });
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();   // sheetData
                writer.WriteEndElement();   // worksheet
            }
            workbookPart.Workbook.AppendChild(new Sheets(new Sheet { Name = "大表", SheetId = 1, Id = workbookPart.GetIdOfPart(worksheetPart) }));
            workbookPart.Workbook.Save();
        }
        return path;
    }
}
