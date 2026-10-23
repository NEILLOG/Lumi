using System.ComponentModel;
using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.Notes;
using Lumi.Core.Tools;

namespace Lumi.Tools.Notes;

public sealed record ConversationSearchArgs(
    [property: Description("要搜尋的主題、關鍵字或短句（最多 100 字）")] string? Query);

/// <summary>
/// 搜尋過去對話與筆記（舊版 <c>conversation_search</c>，M10 10B）。來源：對話（排除目前這則）與近一年的筆記，
/// 合併後依日期由新到舊，最多 10 筆。其中一個來源失敗時只影響它自己，另一個照常回傳。
/// </summary>
public sealed class ConversationSearchTool(IConversationStore conversations, INoteStore notes, NoteOptions options) : ToolBase<ConversationSearchArgs>
{
    private const string NoResultMessage = "找不到符合的對話摘要或筆記。請改用同義詞、較短的關鍵字或更具體的主題。";
    private const string NoContentMessage = "（只有標題符合，沒有內容片段）";

    public override ToolDescriptor Descriptor { get; } = new(
        "conversation_search", "搜尋過去對話",
        "搜尋過去的對話與筆記（筆記只看近一年），找出和某個主題有關的內容。使用者問「我之前聊過…」「上次我們說的…」「我記過…嗎」時使用。不會包含目前這則對話。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(15));

    protected override ToolPreview Describe(ConversationSearchArgs args, ToolContext context) =>
        new(string.IsNullOrWhiteSpace(args.Query) ? null : args.Query.Trim(), null);

    protected override async Task<ToolResult> InvokeAsync(ConversationSearchArgs args, ToolContext context, CancellationToken ct)
    {
        var query = args.Query?.Trim() ?? "";
        if (query.Length == 0)
            return ToolResult.Error("請提供要搜尋的主題或關鍵字。");
        if (query.Length > options.MaxSearchQueryChars)
            return ToolResult.Error($"搜尋字串過長（上限 {options.MaxSearchQueryChars} 字）。");

        var zone = context.Time.LocalTimeZone;
        var items = new List<SearchItem>();
        var conversationFailed = false;
        var noteFailed = false;

        try
        {
            var hits = await conversations.SearchAsync(query, options.SearchConversationScanLimit, ct);
            foreach (var hit in hits.Where(h => h.Conversation.Id != context.ConversationId))
            {
                var local = TimeZoneInfo.ConvertTime(hit.Conversation.UpdatedAt, zone);
                items.Add(new SearchItem(DateOnly.FromDateTime(local.DateTime), "對話", hit.Conversation.Title, hit.Snippet));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 對話儲存層的錯誤（資料庫忙碌、損毀…）只影響對話這一來源，筆記照常搜尋
            conversationFailed = true;
        }

        try
        {
            var today = NoteDates.Today(context);
            var days = await notes.ReadAsync(today.AddDays(-(options.SearchNoteDays - 1)), today, query, ct);
            foreach (var day in days)
            {
                foreach (var entry in day.Entries)
                    items.Add(new SearchItem(day.Date, "筆記", NoteTitle(entry), entry.Content));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is NoteStoreException or IOException or UnauthorizedAccessException)
        {
            noteFailed = true;
        }

        if (conversationFailed && noteFailed)
            return ToolResult.Error("無法搜尋對話與筆記。");

        var top = items.OrderByDescending(i => i.Date).Take(options.MaxSearchResults).ToList();
        if (top.Count == 0)
            return ToolResult.Ok(NoResultMessage, "沒有結果");

        var text = string.Join("\n", top.Select(Format));
        return ToolResult.Ok(text, $"找到 {top.Count} 筆");
    }

    private string Format(SearchItem item)
    {
        var body = item.Body.Length == 0 ? NoContentMessage : Clip(item.Body, options.MaxSnippetChars);
        return $"來源：{item.Source}；日期：{NoteDates.Format(item.Date)}；標題：{item.Title}\n{body}\n---";
    }

    private static string NoteTitle(NoteEntry entry)
    {
        var parts = new List<string>();
        if (entry.Time is { } time)
            parts.Add(time.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(entry.Category))
            parts.Add(entry.Category);
        return parts.Count == 0 ? "筆記" : string.Join(" ", parts);
    }

    private static string Clip(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

    private sealed record SearchItem(DateOnly Date, string Source, string Title, string Body);
}
