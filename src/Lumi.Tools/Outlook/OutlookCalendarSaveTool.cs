using System.ComponentModel;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;

namespace Lumi.Tools.Outlook;

public sealed record OutlookCalendarSaveArgs(
    [property: Description("行程主旨")] string Subject,
    [property: Description("開始時間：yyyy-MM-dd 或 yyyy-MM-ddTHH:mm（本機時間）")] string Start,
    [property: Description("結束時間，格式同 start")] string End,
    [property: Description("要修改的行程代號（e 開頭）；沒給就是新增。只能修改自己建立、沒有與會者的行程")] string? Id = null,
    [property: Description("是否為全天行程；全天時 start、end 只看日期（end 是結束那一天，含當天）")] bool? AllDay = null,
    [property: Description("地點")] string? Location = null,
    [property: Description("備註內容，純文字")] string? Body = null,
    [property: Description("提醒要提前幾分鐘，0 到 10,080；沒給用 Outlook 的預設")] int? ReminderMinutes = null);

/// <summary>新增或修改使用者自己的行程（M30 設計 1、5、6）。寫入，只限工作台。</summary>
public sealed class OutlookCalendarSaveTool(IOutlookClient client) : OutlookToolBase<OutlookCalendarSaveArgs>(client)
{
    private const int MaxReminderMinutes = 10_080;

    public override ToolDescriptor Descriptor { get; } = new(
        "outlook_calendar_save", "儲存行程",
        "新增使用者自己的行程，或修改自己建立、沒有與會者的行程（用 id，代號 e 開頭）。" +
        "只用在沒有與會者的個人行程；要約別人開會請用 outlook_meeting_draft（只建草稿）。" +
        "start、end 用 yyyy-MM-dd 或 yyyy-MM-ddTHH:mm。要修改會議會被拒絕，請使用者自己在 Outlook 修改。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(40),
        Group: ToolGroups.Outlook, Summary: "新增或修改你自己的行程。",
        OptInWarning: OutlookToolText.OptInWarning);

    protected override ToolPreview Describe(OutlookCalendarSaveArgs args, ToolContext context)
    {
        if (!TryBuild(args, out var request, out _))
            return ToolPreview.None;

        var lines = new List<string>
        {
            $"時間：{OutlookToolSupport.FormatRange(request.Start, request.End, request.AllDay)}",
        };
        if (request.Location is not null)
            lines.Add($"地點：{request.Location}");
        if (request.Id is not null)
            lines.Add($"修改既有行程 {request.Id}");
        return new ToolPreview(request.Subject, string.Join("\n", lines));
    }

    protected override async Task<ToolResult> RunAsync(OutlookCalendarSaveArgs args, ToolContext context, CancellationToken ct)
    {
        if (!TryBuild(args, out var request, out var error))
            return ToolResult.Error(error!, "參數不正確");

        var saved = await Client.SaveEventAsync(context.ConversationId, request, ct);
        var when = OutlookToolSupport.FormatRange(saved.Start, saved.End, saved.AllDay);
        return request.Id is null
            ? ToolResult.Ok($"已新增行程：{saved.Subject}（{when}）。代號 {saved.Id}。", "已新增行程")
            : ToolResult.Ok($"已修改行程 {saved.Id}：{saved.Subject}（{when}）。", "已修改行程");
    }

    /// <summary>檢查參數並組成 client 的請求；Describe 與 RunAsync 共用。</summary>
    private static bool TryBuild(OutlookCalendarSaveArgs args, out CalendarSaveRequest request, out string? error)
    {
        request = null!;
        error = null;

        var subject = (args.Subject ?? "").Trim();
        if (subject.Length == 0)
        {
            error = "行程要有主旨。";
            return false;
        }

        if (OutlookToolSupport.ParseDateTime(args.Start, out var start, out _) is { } startError)
        {
            error = startError;
            return false;
        }
        if (OutlookToolSupport.ParseDateTime(args.End, out var end, out _) is { } endError)
        {
            error = endError;
            return false;
        }

        var allDay = args.AllDay ?? false;
        if (allDay)
        {
            // 全天：只看日期，結束日含當天（client 收到的是不含的結束時間）
            start = start.Date;
            end = end.Date;
            if (end < start)
            {
                error = "結束日期要和開始日期相同或更晚。";
                return false;
            }
            end = end.AddDays(1);
        }
        else if (end <= start)
        {
            error = "結束時間要晚於開始時間。";
            return false;
        }

        var reminder = args.ReminderMinutes;
        if (reminder is < 0 or > MaxReminderMinutes)
        {
            error = "reminder_minutes 要在 0 到 10,080 之間。";
            return false;
        }

        var id = string.IsNullOrWhiteSpace(args.Id) ? null : args.Id.Trim();
        var location = string.IsNullOrWhiteSpace(args.Location) ? null : args.Location.Trim();
        var body = string.IsNullOrWhiteSpace(args.Body) ? null : args.Body;

        request = new CalendarSaveRequest(id, subject, start, end, allDay, location, body, reminder);
        return true;
    }
}
