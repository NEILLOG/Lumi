using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Lumi.Core.Documents;

namespace Lumi.Documents;

using CoreDocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名，這裡明確指向 Core 的列舉

/// <summary>
/// Excel（.xlsx、.xlsm）讀取器（M10 C4）。輸出工作表概覽，再輸出被選到那張工作表的 Markdown 表格（列號＋欄字母）。
/// 工作表用 XmlReader 串流讀，只讀到要顯示的範圍底部；共用字串表有字元上限。公式只顯示快取值，巨集不執行。
/// </summary>
internal sealed class ExcelDocumentReader : IDocumentFormatReader
{
    /// <summary>一次最多顯示的列數與欄數（超過就在結尾說明還有多少沒顯示）。</summary>
    internal const int MaxRows = 200;
    internal const int MaxColumns = 30;

    /// <summary>單一儲存格最多顯示的字元數（超過加 …）。</summary>
    internal const int MaxCellChars = 500;

    /// <summary>共用字串表的字元總數上限（500 萬）。</summary>
    internal const long MaxSharedStringChars = 5_000_000;

    internal const string UsageMessage = "範圍格式不正確，請用 A1 範圍（例如 A1:F50）、單一儲存格（例如 B3）、整欄（例如 A:C）或整列（例如 5:10）。";
    internal const string NoVisibleSheetMessage = "這個活頁簿沒有可見的工作表。";
    internal const string SharedStringLimitMessage = "這份 Excel 的文字內容過多（共用字串超過 500 萬字元），已拒絕讀取。";

    public CoreDocumentFormat Format => CoreDocumentFormat.Excel;

    public bool Supports(string extension) =>
        extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct) =>
        Task.FromResult(Read(request, ct));

    private static DocumentReadResult Read(DocumentReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ExcelRange? requestedRange = null;
        if (!string.IsNullOrWhiteSpace(request.Range))
        {
            if (!ExcelAddress.TryParseRange(request.Range, out var parsed))
                return Error(UsageMessage);
            requestedRange = parsed;
        }

        OpenXmlSafety.Verify(request.FullPath);
        using var stream = new FileStream(request.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = SpreadsheetDocument.Open(stream, false, OpenXmlSafety.CreateOpenSettings());
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("活頁簿缺少 workbook 部件。");
        var date1904 = workbookPart.Workbook!.WorkbookProperties?.Date1904?.Value ?? false;

        var sheets = (workbookPart.Workbook!.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
            .Select(sheet => new SheetInfo(
                sheet.Name?.Value ?? "",
                IsVisible(sheet.State?.Value),
                sheet.Id?.Value is { } id ? workbookPart.GetPartById(id) as WorksheetPart : null))
            .ToList();
        var visibleSheets = sheets.Where(sheet => sheet.Visible).ToList();

        var target = SelectSheet(request.Sheet, sheets, visibleSheets, out var selectionError);
        if (target is null)
            return Error(selectionError!);

        var context = new ExcelWorkbookContext(workbookPart, date1904);
        var output = new StringBuilder();
        output.Append(FormattableString.Invariant($"[概覽] 工作表 {sheets.Count} 張（可見 {visibleSheets.Count}）\n"));

        ExcelRange? targetUsed = null;
        foreach (var sheet in visibleSheets)
        {
            ct.ThrowIfCancellationRequested();
            var used = ExcelXmlParts.GetUsedRange(sheet.Part);
            if (ReferenceEquals(sheet, target))
                targetUsed = used;
            output.Append("- ").Append(sheet.Name).Append("：使用範圍 ").Append(DescribeUsed(used))
                .Append(FormattableString.Invariant($"（{CountOf(used, byRows: true):N0} 列 × {CountOf(used, byRows: false):N0} 欄）\n"));
        }

        var hiddenCount = sheets.Count - visibleSheets.Count;
        if (hiddenCount > 0)
            output.Append(FormattableString.Invariant($"另有 {hiddenCount} 張隱藏的工作表（未顯示）\n"));
        output.Append("---\n");

        var shownRows = WriteSheet(target, requestedRange, targetUsed, context, output, ct);
        var content = output.ToString().TrimEnd('\n');
        return new DocumentReadResult(false, CoreDocumentFormat.Excel, content, $"Excel · {target.Name} · {shownRows} 列", false, null);
    }

    /// <summary>寫出被選到的工作表：標題、表格與結尾的說明；回傳實際輸出的列數。</summary>
    private static int WriteSheet(SheetInfo target, ExcelRange? requestedRange, ExcelRange? used, ExcelWorkbookContext context, StringBuilder output, CancellationToken ct)
    {
        var bounds = requestedRange is { } range ? ResolveBounds(range, used) : used;
        if (bounds is not { } wanted)
        {
            output.Append("## ").Append(target.Name).Append("　（沒有內容）\n");
            return 0;
        }

        var top = wanted.Top;
        var left = wanted.Left;
        var bottom = Math.Min(wanted.Bottom, top + MaxRows - 1);
        var right = Math.Min(wanted.Right, left + MaxColumns - 1);
        var remainingRows = (wanted.Bottom - wanted.Top + 1) - (bottom - top + 1);
        var remainingColumns = (wanted.Right - wanted.Left + 1) - (right - left + 1);

        output.Append("## ").Append(target.Name).Append('　').Append(ExcelAddress.Display(top, left, bottom, right)).Append("\n\n");

        var rows = new StringBuilder();
        var shownRows = 0;
        if (target.Part is { } part)
        {
            var window = new ExcelRange(ExcelRangeKind.Cells, top, left, bottom, right);
            ExcelXmlParts.ReadWindow(part, window, context, ct, (rowNumber, values) =>
            {
                if (Array.TrueForAll(values, string.IsNullOrEmpty))
                    return;   // 完全空白的列略過，但列號保持真實列號
                shownRows++;
                rows.Append("| ").Append(rowNumber.ToString(CultureInfo.InvariantCulture));
                foreach (var value in values)
                    rows.Append(" | ").Append(EscapeCell(value ?? ""));
                rows.Append(" |\n");
            });
        }

        if (shownRows == 0)
        {
            output.Append("（這個範圍沒有內容）\n");
        }
        else
        {
            output.Append("| 列");
            for (var column = left; column <= right; column++)
                output.Append(" | ").Append(ExcelAddress.ColumnName(column));
            output.Append(" |\n|");
            for (var column = left - 1; column <= right; column++)
                output.Append("---|");
            output.Append('\n').Append(rows);
        }

        if (remainingRows > 0 || remainingColumns > 0)
        {
            var parts = new List<string>();
            if (remainingRows > 0)
                parts.Add(FormattableString.Invariant($"{remainingRows:N0} 列"));
            if (remainingColumns > 0)
                parts.Add(FormattableString.Invariant($"{remainingColumns:N0} 欄"));

            var example = remainingRows > 0
                ? ExcelAddress.Display(bottom + 1, left, Math.Min(bottom + MaxRows, ExcelAddress.MaxRow), right)
                : ExcelAddress.Display(top, right + 1, bottom, Math.Min(right + MaxColumns, ExcelAddress.MaxColumn));
            output.Append('\n').Append(FormattableString.Invariant($"（還有 {string.Join("、", parts)}未顯示，請用 range 指定範圍，例如 {example}）\n"));
        }

        return shownRows;
    }

    /// <summary>把使用者給的範圍換成實際的矩形：整欄用到底的使用範圍列數，整列用使用範圍的欄範圍。</summary>
    private static ExcelRange ResolveBounds(ExcelRange range, ExcelRange? used) => range.Kind switch
    {
        ExcelRangeKind.Columns => new ExcelRange(ExcelRangeKind.Cells, 1, range.Left, used?.Bottom ?? 1, range.Right),
        ExcelRangeKind.Rows => new ExcelRange(ExcelRangeKind.Cells, range.Top, used?.Left ?? 1, range.Bottom, used?.Right ?? 1),
        _ => range,
    };

    private static SheetInfo? SelectSheet(string? requested, List<SheetInfo> sheets, List<SheetInfo> visibleSheets, out string? error)
    {
        error = null;
        var name = requested?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            if (visibleSheets.Count > 0)
                return visibleSheets[0];
            error = NoVisibleSheetMessage;
            return null;
        }

        var match = sheets.FirstOrDefault(sheet => string.Equals(sheet.Name, name, StringComparison.OrdinalIgnoreCase));
        var visibleList = visibleSheets.Count == 0 ? NoVisibleSheetMessage : "可見的工作表：" + string.Join("、", visibleSheets.Select(sheet => sheet.Name)) + "。";
        if (match is null)
        {
            error = $"找不到工作表「{name}」。{visibleList}";
            return null;
        }
        if (!match.Visible)
        {
            error = $"工作表「{name}」是隱藏的，無法讀取。{visibleList}";
            return null;
        }
        return match;
    }

    private static bool IsVisible(SheetStateValues? state) => state is null || state == SheetStateValues.Visible;

    private static string DescribeUsed(ExcelRange? used) =>
        used is { } range ? ExcelAddress.Display(range.Top, range.Left, range.Bottom, range.Right) : "無";

    private static int CountOf(ExcelRange? used, bool byRows) => used is { } range
        ? byRows ? range.Bottom - range.Top + 1 : range.Right - range.Left + 1
        : 0;

    /// <summary>儲存格文字轉成 Markdown 表格的一格：超過 500 字元截斷加 …，| 跳脫，換行改 &lt;br&gt;。</summary>
    private static string EscapeCell(string value)
    {
        var text = value;
        if (text.Length > MaxCellChars)
        {
            var cut = MaxCellChars;
            if (char.IsHighSurrogate(text[cut - 1]))
                cut--;   // 不把代理對切成兩半
            text = text[..cut] + "…";
        }
        return text.Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static DocumentReadResult Error(string message) => new(true, CoreDocumentFormat.Excel, message, null, false, null);

    private sealed record SheetInfo(string Name, bool Visible, WorksheetPart? Part);
}
