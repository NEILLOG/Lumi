using System.ComponentModel;
using System.Text.Json;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Tools.Reminders;

public sealed record ListRemindersArgs(
    [property: Description("狀態篩選：pending（待觸發，預設）、completed（已完成）、cancelled（已取消）、missed（錯過：Lumi 沒在執行時過了時間的一次性提醒）、all（全部）")] string? Status = null,
    [property: Description("類型篩選：reminder（提醒）、timer（計時器）、schedule（週期排程）、all（全部，預設）")] string? Kind = null,
    [property: Description("最多回傳幾筆，1～100，省略為 20；超出範圍會自動調整到範圍內")] int? MaxResults = null);

/// <summary>列出提醒（舊版 <c>reminder_list</c>，M10 10E2）：預設只列待觸發的項目。</summary>
public sealed class ListRemindersTool(IReminderService reminders) : ToolBase<ListRemindersArgs>
{
    private const int DefaultMaxResults = 20;
    private const int MinMaxResults = 1;
    private const int MaxMaxResults = 100;

    private static readonly Dictionary<string, ScheduledStatus> StatusNames = new()
    {
        ["pending"] = ScheduledStatus.Pending,
        ["completed"] = ScheduledStatus.Completed,
        ["cancelled"] = ScheduledStatus.Cancelled,
        ["missed"] = ScheduledStatus.Missed,
    };

    private static readonly Dictionary<string, ScheduledKind> KindNames = new()
    {
        ["reminder"] = ScheduledKind.Reminder,
        ["timer"] = ScheduledKind.Timer,
        ["schedule"] = ScheduledKind.Schedule,
    };

    public override ToolDescriptor Descriptor { get; } = new(
        "list_reminders", "列出提醒",
        "列出使用者的提醒、計時器與週期排程，預設只列還沒響的項目。使用者問「我有哪些提醒」「還有什麼還沒響」時使用；"
        + "要取消或停用某一項之前，可以先用這個工具找到它的 id。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(10), Group: ToolGroups.Reminders, Summary: "列出目前的提醒與計時器。");

    protected override async Task<ToolResult> InvokeAsync(ListRemindersArgs args, ToolContext context, CancellationToken ct)
    {
        if (!TryParseStatus(args.Status, out var statuses, out var statusError))
            return ToolResult.Error(statusError!, "參數不正確");
        if (!TryParseKind(args.Kind, out var kinds, out var kindError))
            return ToolResult.Error(kindError!, "參數不正確");

        var limit = Math.Clamp(args.MaxResults ?? DefaultMaxResults, MinMaxResults, MaxMaxResults);
        var items = await reminders.ListAsync(new ReminderQuery(statuses, kinds, limit), ct);

        var rows = items.Select(ToRow).ToList();
        var summary = $"{rows.Count} 個提醒";
        return ToolResult.Ok(JsonSerializer.Serialize(rows, ReminderFormat.JsonOptions), summary);
    }

    private static ReminderListItem ToRow(ScheduledItem item)
    {
        var zone = ReminderFormat.ZoneOf(item.TimeZoneId);
        var recurrence = item.Trigger is RecurrenceTrigger r ? ScheduleCalculator.Describe(r, item.TimeZoneId) : null;
        return new ReminderListItem(
            item.Id,
            KindName(item.Kind),
            item.Title,
            item.Message,
            StatusName(item.Status),
            item.Enabled,
            ReminderFormat.Iso(item.NextFireAt, zone),
            ReminderFormat.Iso(item.LastFiredAt, zone),
            recurrence);
    }

    /// <summary>篩選值去頭尾空白、不分大小寫；未知值回清楚的錯誤。</summary>
    private static bool TryParseStatus(string? raw, out IReadOnlyCollection<ScheduledStatus>? statuses, out string? error)
    {
        var text = Normalize(raw, "pending");
        if (text == "all")
        {
            statuses = null;
            error = null;
            return true;
        }
        if (StatusNames.TryGetValue(text, out var status))
        {
            statuses = [status];
            error = null;
            return true;
        }

        statuses = null;
        error = "status 只能是 pending、completed、cancelled、missed 或 all。";
        return false;
    }

    private static bool TryParseKind(string? raw, out IReadOnlyCollection<ScheduledKind>? kinds, out string? error)
    {
        var text = Normalize(raw, "all");
        if (text == "all")
        {
            kinds = null;
            error = null;
            return true;
        }
        if (KindNames.TryGetValue(text, out var kind))
        {
            kinds = [kind];
            error = null;
            return true;
        }

        kinds = null;
        error = "kind 只能是 reminder、timer、schedule 或 all。";
        return false;
    }

    private static string Normalize(string? raw, string fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim().ToLowerInvariant();

    private static string KindName(ScheduledKind kind) => kind.ToString().ToLowerInvariant();

    private static string StatusName(ScheduledStatus status) => status.ToString().ToLowerInvariant();

    private sealed record ReminderListItem(
        string Id,
        string Kind,
        string Title,
        string Message,
        string Status,
        bool Enabled,
        string? NextFireAt,
        string? LastFiredAt,
        string? Recurrence);
}
