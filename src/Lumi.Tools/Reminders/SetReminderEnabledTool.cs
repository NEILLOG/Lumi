using System.ComponentModel;
using System.Text.Json;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Tools.Reminders;

public sealed record SetReminderEnabledArgs(
    [property: Description("提醒、計時器或排程的 id（8 碼十六進位），可由 list_reminders 取得")] string Id,
    [property: Description("true 為啟用、false 為停用。停用後不會再觸發，但項目不會被刪除")] bool Enabled);

/// <summary>啟用或停用待觸發的提醒（舊版 <c>schedule_enable</c>、<c>schedule_disable</c>，M10 10E2）。</summary>
public sealed class SetReminderEnabledTool(IReminderService reminders) : ToolBase<SetReminderEnabledArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "set_reminder_enabled", "啟用或停用提醒",
        "暫停（enabled 為 false）或恢復（enabled 為 true）一則還沒結束的提醒、計時器或週期排程，不會刪除它。"
        + "已經完成、取消或錯過的項目無法啟用或停用。只在使用者明確要求暫停或恢復時使用。",
        ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(10));

    protected override ToolPreview Describe(SetReminderEnabledArgs args, ToolContext context) =>
        new(string.IsNullOrWhiteSpace(args.Id) ? null : args.Id.Trim(), args.Enabled ? "啟用" : "停用");

    protected override async Task<ToolResult> InvokeAsync(SetReminderEnabledArgs args, ToolContext context, CancellationToken ct)
    {
        var id = args.Id?.Trim() ?? "";
        if (id.Length == 0)
            return ToolResult.Error("id 不能為空。", "缺少 id");

        ScheduledItem? item;
        try
        {
            item = await reminders.SetEnabledAsync(id, args.Enabled, ct);
        }
        catch (ReminderException e)
        {
            // 已結束或啟用時時間已過：訊息是給模型看的
            return ToolResult.Error(e.Message, "無法變更");
        }

        if (item is null)
            return ToolResult.Error("找不到指定的提醒項目。", "找不到");

        var zone = ReminderFormat.ZoneOf(item.TimeZoneId);
        var message = args.Enabled ? null : "已停用，後續將不會再觸發。";
        var response = new SetEnabledResponse(item.Id, item.Enabled, ReminderFormat.Iso(item.NextFireAt, zone), message);
        var summary = $"{(args.Enabled ? "已啟用" : "已停用")}「{item.Title}」";
        return ToolResult.Ok(JsonSerializer.Serialize(response, ReminderFormat.JsonOptions), summary);
    }

    private sealed record SetEnabledResponse(string Id, bool Enabled, string? NextFireAt, string? Message);
}
