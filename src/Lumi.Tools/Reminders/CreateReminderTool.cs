using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Tools.Reminders;

public sealed record CreateReminderArgs(
    [property: Description("提醒標題（必填，1～100 字）")] string Title,
    [property: Description("提醒內容（必填，1～500 字），時間到了會顯示給使用者看")] string Message,
    [property: Description("一次性提醒的觸發時間。ISO 8601 格式，建議含時區偏移（例如 2026-10-25T09:00:00+08:00）；不含偏移時視為 time_zone（省略就是本機時區）的當地時間（例如 2026-10-25T09:00）。必須晚於現在。與 recurrence 只能擇一")] string? At = null,
    [property: Description("週期提醒的種類：daily（每天）、weekdays（週一至週五）、weekly（每週特定的星期，要搭配 days）、monthly（每月固定的日期，用 day_of_month）。與 at 只能擇一；使用時要搭配 time")] string? Recurrence = null,
    [property: Description("週期提醒每天觸發的時間，24 小時制 HH:mm（例如 09:00、18:30）。recurrence 有值時必填")] string? Time = null,
    [property: Description("weekly 用的星期幾，字串陣列，例如 [\"monday\", \"friday\"] 或 [\"1\", \"5\"]。英文星期不分大小寫；數字 1＝週一…7＝週日（0 也是週日）"), JsonConverter(typeof(LooseStringListConverter))] IReadOnlyList<string>? Days = null,
    [property: Description("monthly 用的每月日期，1～31，省略為 1。當月沒有那一天時（例如 2 月的 30 日）改用當月最後一天")] int? DayOfMonth = null,
    [property: Description("時區的 IANA 名稱，例如 Asia/Taipei、America/New_York；省略就是使用者電腦的時區")] string? TimeZone = null);

/// <summary>建立提醒（舊版 <c>reminder_create</c>、<c>schedule_create</c>，M10 10E1）：一次性用 <c>at</c>，週期用 <c>recurrence</c>。</summary>
public sealed class CreateReminderTool(IReminderService reminders, ReminderOptions options) : ToolBase<CreateReminderArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "create_reminder", "建立提醒",
        "建立一則提醒，時間到了會在 Lumi 裡跳出通知。只在使用者明確要求提醒時使用，例如「十分鐘後提醒我…」「每天早上九點提醒我…」。"
        + "相對時間（「十分鐘後」「明天早上」）要先用 get_current_time 取得現在時間，再換算成 at。"
        + "一次性提醒用 at；每天、平日、每週或每月的重複提醒用 recurrence 加 time。"
        + "Lumi 沒有在執行時（例如程式沒開）不會響，不要承諾一定會準時叫使用者。設定後會把解析後的時間顯示給使用者確認。",
        ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(10), Group: ToolGroups.Reminders, Summary: "在指定時間提醒你。");

    protected override ToolPreview Describe(CreateReminderArgs args, ToolContext context)
    {
        var target = string.IsNullOrWhiteSpace(args.Title) ? null : args.Title.Trim();
        if (!ReminderFormat.TryResolveZone(args.TimeZone, out var zone, out _)
            || !ReminderParser.TryParseTrigger(args, zone, out var trigger, out _))
            return new ToolPreview(target, null);

        var now = context.Time.GetUtcNow();
        var detail = trigger switch
        {
            OneTimeTrigger oneTime => $"{ReminderFormat.Local(oneTime.At, zone)} · {zone.Id}",
            RecurrenceTrigger recurrence => DescribeRecurrence(recurrence, zone, now),
            _ => null,
        };
        return new ToolPreview(target, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(CreateReminderArgs args, ToolContext context, CancellationToken ct)
    {
        if (!ReminderFormat.TryCheckText(args.Title, "title", options.MaxTitleChars, out var title, out var titleError))
            return ToolResult.Error(titleError!, "參數不正確");
        if (!ReminderFormat.TryCheckText(args.Message, "message", options.MaxMessageChars, out var message, out var messageError))
            return ToolResult.Error(messageError!, "參數不正確");
        if (!ReminderFormat.TryResolveZone(args.TimeZone, out var zone, out var zoneError))
            return ToolResult.Error(zoneError!, "時區不正確");
        if (!ReminderParser.TryParseTrigger(args, zone, out var trigger, out var error))
            return ToolResult.Error(error!, "參數不正確");

        var kind = trigger is RecurrenceTrigger ? ScheduledKind.Schedule : ScheduledKind.Reminder;
        ScheduledItem item;
        try
        {
            item = await reminders.CreateReminderAsync(
                new NewReminder(title, message, trigger!, zone.Id, kind, ScheduledCreator.Agent), ct);
        }
        catch (ReminderException e)
        {
            // 業務錯誤（時間已過、達上限…）：訊息是給模型看的
            return ToolResult.Error(e.Message, "無法建立");
        }

        var next = item.NextFireAt!.Value;
        var summary = $"已設定提醒 · {ReminderFormat.Short(next, zone)}";
        if (item.Trigger is not RecurrenceTrigger recurrence)
        {
            var created = new CreatedReminder(
                item.Id, item.Title, ReminderFormat.Iso(next, zone)!, ReminderFormat.Local(next, zone));
            return ToolResult.Ok(JsonSerializer.Serialize(created, ReminderFormat.JsonOptions), summary);
        }

        var occurrences = ScheduleCalculator.NextOccurrences(recurrence, zone.Id, context.Time.GetUtcNow(), 3)
            .Select(t => ReminderFormat.Local(t, zone))
            .ToList();
        var schedule = new CreatedReminder(
            item.Id, item.Title, ReminderFormat.Iso(next, zone)!, ReminderFormat.Local(next, zone),
            ScheduleCalculator.Describe(recurrence, zone.Id), occurrences);
        return ToolResult.Ok(JsonSerializer.Serialize(schedule, ReminderFormat.JsonOptions), summary);
    }

    /// <summary>確認卡片的週期說明：「每週一、五 09:00，接下來：2026-10-26（週一）09:00、…」。</summary>
    private static string? DescribeRecurrence(RecurrenceTrigger recurrence, TimeZoneInfo zone, DateTimeOffset now)
    {
        var next = ScheduleCalculator.NextOccurrences(recurrence, zone.Id, now, 3);
        var times = next.Count == 0
            ? "（無法計算）"
            : string.Join("、", next.Select(t => ReminderFormat.Local(t, zone)));
        return $"{ScheduleCalculator.Describe(recurrence, zone.Id)}，接下來：{times}";
    }

    /// <summary>建立成功的回傳（一次性與週期共用，週期才有 recurrence 與 next_occurrences）。</summary>
    private sealed record CreatedReminder(
        string Id,
        string Title,
        string NextFireAt,
        string NextFireAtLocal,
        string? Recurrence = null,
        IReadOnlyList<string>? NextOccurrences = null);
}
