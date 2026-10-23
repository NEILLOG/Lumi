using System.Globalization;
using System.Text;

namespace Lumi.Documents;

/// <summary>範圍的形式：一般矩形（含單一儲存格）、整欄（<c>A:C</c>）或整列（<c>5:10</c>）。</summary>
internal enum ExcelRangeKind { Cells, Columns, Rows }

/// <summary>
/// 解析後的 Excel 範圍（1 起算、頭尾含）。<see cref="ExcelRangeKind.Columns"/> 只用 Left／Right，<see cref="ExcelRangeKind.Rows"/> 只用 Top／Bottom。
/// </summary>
internal readonly record struct ExcelRange(ExcelRangeKind Kind, int Top, int Left, int Bottom, int Right);

/// <summary>A1 位址的解析與顯示（M10 C4）。</summary>
internal static class ExcelAddress
{
    /// <summary>Excel 的列數上限（1048576）。</summary>
    public const int MaxRow = 1_048_576;

    /// <summary>Excel 的欄數上限（XFD，16384）。</summary>
    public const int MaxColumn = 16_384;

    /// <summary>解析 <c>A1:F50</c>、<c>B3</c>、<c>A:C</c>、<c>5:10</c>（大小寫不分，反過來寫的範圍會自動排好）。</summary>
    public static bool TryParseRange(string text, out ExcelRange range)
    {
        range = default;
        var parts = text.Trim().ToUpperInvariant().Split(':');
        switch (parts.Length)
        {
            case 1:
                if (!TryParseCell(parts[0], out var single, out var singleColumn))
                    return false;
                range = new ExcelRange(ExcelRangeKind.Cells, single, singleColumn, single, singleColumn);
                return true;

            case 2 when IsDigits(parts[0]) && IsDigits(parts[1]):
                if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
                    || first < 1 || second < 1 || first > MaxRow || second > MaxRow)
                    return false;
                range = new ExcelRange(ExcelRangeKind.Rows, Math.Min(first, second), 0, Math.Max(first, second), 0);
                return true;

            case 2 when IsLetters(parts[0]) && IsLetters(parts[1]):
                var leftColumn = ColumnIndex(parts[0]);
                var rightColumn = ColumnIndex(parts[1]);
                if (leftColumn < 1 || rightColumn < 1 || leftColumn > MaxColumn || rightColumn > MaxColumn)
                    return false;
                range = new ExcelRange(ExcelRangeKind.Columns, 0, Math.Min(leftColumn, rightColumn), 0, Math.Max(leftColumn, rightColumn));
                return true;

            case 2:
                if (!TryParseCell(parts[0], out var r1, out var c1) || !TryParseCell(parts[1], out var r2, out var c2))
                    return false;
                range = new ExcelRange(ExcelRangeKind.Cells, Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
                return true;

            default:
                return false;
        }
    }

    /// <summary>解析單一儲存格位址（例如 <c>B3</c>）；欄最多 3 個字母，列最多 7 位數，超出上限回 false。</summary>
    public static bool TryParseCell(ReadOnlySpan<char> text, out int row, out int column)
    {
        row = 0;
        column = 0;
        var i = 0;
        var col = 0;
        while (i < text.Length && char.IsAsciiLetter(text[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(text[i]) - 'A' + 1);
            i++;
        }
        if (i is 0 or > 3)
            return false;

        var digits = 0;
        var number = 0;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
        {
            number = number * 10 + (text[i] - '0');
            if (++digits > 7)
                return false;
        }
        if (digits == 0 || i != text.Length || number < 1 || number > MaxRow || col < 1 || col > MaxColumn)
            return false;

        row = number;
        column = col;
        return true;
    }

    /// <summary>欄號轉字母（1 → A、27 → AA）。</summary>
    public static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        for (var n = column; n > 0; n = (n - 1) / 26)
            builder.Insert(0, (char)('A' + (n - 1) % 26));
        return builder.ToString();
    }

    /// <summary>矩形的顯示文字：單一儲存格顯示 <c>B3</c>，其他顯示 <c>A1:F50</c>。</summary>
    public static string Display(int top, int left, int bottom, int right)
    {
        var first = ColumnName(left) + top.ToString(CultureInfo.InvariantCulture);
        if (top == bottom && left == right)
            return first;
        return first + ":" + ColumnName(right) + bottom.ToString(CultureInfo.InvariantCulture);
    }

    private static int ColumnIndex(string letters)
    {
        var column = 0;
        foreach (var c in letters)
            column = column * 26 + (c - 'A' + 1);
        return column;
    }

    private static bool IsDigits(string text) => text.Length is >= 1 and <= 7 && text.All(char.IsAsciiDigit);

    private static bool IsLetters(string text) => text.Length is >= 1 and <= 3 && text.All(char.IsAsciiLetter);
}
