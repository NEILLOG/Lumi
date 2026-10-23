using System.ComponentModel;
using System.Text.Json;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Tools.Reminders;

public sealed record CancelReminderArgs(
    [property: Description("要取消的項目 id（8 碼十六進位，例如 3f9a0c1e），可由 create_reminder、start_timer 或 list_reminders 取得")] string Id);

/// <summary>取消提醒、計時器或排程（舊版 <c>reminder_cancel</c>、<c>timer_cancel</c>，M10 10E2）。</summary>
public sealed class CancelReminderTool(IReminderService reminders) : ToolBase<CancelReminderArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "cancel_reminder", "取消提醒",
        "取消一則還沒響的提醒、計時器或週期排程（用 id）。只在使用者明確要求取消時使用。"
        + "已經完成、取消或錯過的項目不會有變更，會直接回報它目前的狀態。",
        ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(10));

    protected override ToolPreview Describe(CancelReminderArgs args, ToolContext context) =>
        new(string.IsNullOrWhiteSpace(args.Id) ? null : args.Id.Trim(), null);

    protected override async Task<ToolResult> InvokeAsync(CancelReminderArgs args, ToolContext context, CancellationToken ct)
    {
        var id = args.Id?.Trim() ?? "";
        if (id.Length == 0)
            return ToolResult.Error("id 不能為空。", "缺少 id");

        var result = await reminders.CancelAsync(id, ct);
        if (result.Item is not { } item)
            return ToolResult.Error("找不到指定的提醒項目。", "找不到");

        if (!result.Changed)
        {
            // 已經結束：不算錯誤，只回報目前狀態
            var status = item.Status.ToString().ToLowerInvariant();
            var unchanged = new CancelResponse(id, status, $"項目目前狀態為 {status}，未進行變更。");
            return ToolResult.Ok(JsonSerializer.Serialize(unchanged, ReminderFormat.JsonOptions), "已結束，未變更");
        }

        var cancelled = new CancelResponse(id, "cancelled", Cancelled: true);
        return ToolResult.Ok(JsonSerializer.Serialize(cancelled, ReminderFormat.JsonOptions), $"已取消「{item.Title}」");
    }

    private sealed record CancelResponse(string Id, string Status, string? Message = null, bool? Cancelled = null);
}
