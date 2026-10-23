using System.ComponentModel;
using System.Globalization;
using System.Text;
using Lumi.Core.Notes;
using Lumi.Core.Tools;

namespace Lumi.Tools.Notes;

public sealed record ReadNotesArgs(
    [property: Description("起始日期，格式 yyyy-MM-dd；省略就是今天")] string? DateFrom = null,
    [property: Description("結束日期（含），格式 yyyy-MM-dd；省略時與 date_from 相同。日期範圍最多 31 天")] string? DateTo = null,
    [property: Description("只回傳內容或分類含這個關鍵字的記事（最多 50 字）；省略就回傳全部")] string? Keyword = null);

/// <summary>查筆記（舊版 <c>notes_read</c>，M10 10B）：可指定日期範圍與關鍵字，只列出有記事的日期。</summary>
public sealed class ReadNotesTool(INoteStore notes, NoteOptions options) : ToolBase<ReadNotesArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "read_notes", "查筆記",
        "讀取使用者記過的筆記，可指定日期範圍與關鍵字。沒有指定日期時只讀今天；範圍最多 31 天。使用者問「我今天記了什麼」「我上週那個會議記了什麼」時使用。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(10));

    protected override ToolPreview Describe(ReadNotesArgs args, ToolContext context)
    {
        if (!TryResolveRange(args, context, out var from, out var to, out _))
            return ToolPreview.None;
        return new ToolPreview($"{NoteDates.Format(from)} 至 {NoteDates.Format(to)}", null);
    }

    protected override async Task<ToolResult> InvokeAsync(ReadNotesArgs args, ToolContext context, CancellationToken ct)
    {
        if (!TryResolveRange(args, context, out var from, out var to, out var error))
            return ToolResult.Error(error!, "參數不正確");

        var keyword = args.Keyword?.Trim();
        if (keyword is { Length: > 0 } && keyword.Length > options.MaxKeywordChars)
            return ToolResult.Error($"keyword 太長（上限 {options.MaxKeywordChars} 字）。");

        IReadOnlyList<NoteDay> days;
        try
        {
            days = await notes.ReadAsync(from, to, string.IsNullOrEmpty(keyword) ? null : keyword, ct);
        }
        catch (NoteStoreException e)
        {
            return ToolResult.Error($"無法讀取筆記：{e.UserMessage}", "無法讀取");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error("無法讀取筆記。", "無法讀取");
        }

        if (days.Count == 0)
            return ToolResult.Ok("指定範圍內沒有符合的筆記。", "沒有筆記");

        // 每天一個區塊：標題行加上記事行，區塊之間空一行
        var blocks = days.Select(day =>
            string.Join("\n", new[] { "## " + NoteDates.Format(day.Date) }.Concat(day.Entries.Select(FormatEntry))));
        var total = days.Sum(day => day.Entries.Count);
        return ToolResult.Ok(string.Join("\n\n", blocks), $"{total} 則筆記，{days.Count} 天");
    }

    private bool TryResolveRange(ReadNotesArgs args, ToolContext context, out DateOnly from, out DateOnly to, out string? error)
    {
        var today = NoteDates.Today(context);
        if (!NoteDates.TryParse(args.DateFrom, "date_from", today, out from, out error))
        {
            to = default;
            return false;
        }
        if (!NoteDates.TryParse(args.DateTo, "date_to", from, out to, out error))
            return false;
        if (to < from)
        {
            error = "date_to 不可早於 date_from。";
            return false;
        }
        if (to.DayNumber - from.DayNumber + 1 > options.MaxRangeDays)
        {
            error = $"日期範圍最多 {options.MaxRangeDays} 天，請縮小範圍。";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>一則記事：<c>- HH:mm [分類] 內容</c>，續行縮排兩格（和筆記檔的寫法一樣）。</summary>
    private static string FormatEntry(NoteEntry entry)
    {
        var builder = new StringBuilder("- ");
        if (entry.Time is { } time)
            builder.Append(time.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(' ');
        if (!string.IsNullOrEmpty(entry.Category))
            builder.Append('[').Append(entry.Category).Append("] ");

        var lines = entry.Content.Split('\n');
        builder.Append(lines[0]);
        foreach (var line in lines.Skip(1))
            builder.Append("\n  ").Append(line);
        return builder.ToString();
    }
}
