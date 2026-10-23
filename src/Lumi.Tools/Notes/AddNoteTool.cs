using System.ComponentModel;
using System.Text.RegularExpressions;
using Lumi.Core.Notes;
using Lumi.Core.Tools;

namespace Lumi.Tools.Notes;

public sealed record AddNoteArgs(
    [property: Description("要記錄的內容（1～4000 字）")] string Content,
    [property: Description("記到哪一天，格式 yyyy-MM-dd（例如 2026-10-23）。可以是今天往前 30 天到明天；省略就是今天")] string? Date = null,
    [property: Description("選填分類，例如「開發」、「會議」、「待辦」（最多 50 字）")] string? Category = null);

/// <summary>記筆記（舊版 <c>worklog_append</c>，M10 10B）：只追加到當天的筆記檔，不覆寫、不刪除。</summary>
public sealed partial class AddNoteTool(INoteStore notes, NoteOptions options) : ToolBase<AddNoteArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "add_note", "記筆記",
        "把一則內容記到使用者的筆記裡（每天一個檔案，只追加，不覆寫、不刪除）。只在使用者明確要求記錄時使用，例如「幫我記下…」「記一筆…」。",
        ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(10));

    protected override ToolPreview Describe(AddNoteArgs args, ToolContext context)
    {
        var target = NoteDates.TryParse(args.Date, "date", NoteDates.Today(context), out var date, out _)
            ? $"{NoteDates.Format(date)}.md"
            : null;
        var content = args.Content?.Trim();
        var detail = string.IsNullOrEmpty(content) ? null : Clip(content, options.PreviewChars);
        return new ToolPreview(target, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(AddNoteArgs args, ToolContext context, CancellationToken ct)
    {
        var content = args.Content?.Trim() ?? "";
        if (content.Length == 0)
            return ToolResult.Error("content 不可為空。");
        if (content.Length > options.MaxContentChars)
            return ToolResult.Error($"content 太長（上限 {options.MaxContentChars} 字），請精簡後再記。");

        var category = NormalizeCategory(args.Category);
        if (category is not null && category.Length > options.MaxCategoryChars)
            return ToolResult.Error($"category 太長（上限 {options.MaxCategoryChars} 字）。");

        var now = NoteDates.LocalNow(context);
        if (!TryResolveDate(args.Date, NoteDates.Today(context), out var date, out var error))
            return ToolResult.Error(error!);

        try
        {
            await notes.AppendAsync(date, TimeOnly.FromDateTime(now.DateTime), category, content, ct);
        }
        catch (NoteStoreException e)
        {
            return ToolResult.Error($"無法寫入筆記：{e.UserMessage}", "無法寫入");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error("無法寫入筆記，請確認筆記資料夾可以寫入。", "無法寫入");
        }

        return ToolResult.Ok($"已記錄到 {NoteDates.Format(date)} 的筆記。", $"已記到 {date:MM/dd} 的筆記");
    }

    private bool TryResolveDate(string? text, DateOnly today, out DateOnly date, out string? error)
    {
        if (!NoteDates.TryParse(text, "date", today, out date, out error))
            return false;

        var earliest = today.AddDays(-options.MaxPastDays);
        var latest = today.AddDays(options.MaxFutureDays);
        if (date < earliest || date > latest)
        {
            error = $"date 只能是 {NoteDates.Format(earliest)} 到 {NoteDates.Format(latest)} 之間。";
            return false;
        }
        return true;
    }

    /// <summary>合併空白、去掉 <c>[</c> <c>]</c>（它們是檔案格式的分隔符）；結果是空的就當作沒有分類。</summary>
    internal static string? NormalizeCategory(string? raw)
    {
        if (raw is null)
            return null;
        var cleaned = WhiteSpace().Replace(raw.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal), " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string Clip(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
