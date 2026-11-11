using System.ComponentModel;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;

namespace Lumi.Tools.Outlook;

public sealed record OutlookMeetingDraftArgs(
    [property: Description("會議主旨")] string Subject,
    [property: Description("開始時間：yyyy-MM-dd 或 yyyy-MM-ddTHH:mm（本機時間）")] string Start,
    [property: Description("結束時間，格式同 start")] string End,
    [property: Description("必要與會者（至少一人），名稱或電子郵件地址")] string[] Required,
    [property: Description("選用與會者")] string[]? Optional = null,
    [property: Description("地點")] string? Location = null,
    [property: Description("會議內容，純文字")] string? Body = null);

/// <summary>建立會議邀請草稿，在 Outlook 打開，不寄出（D15：只做草稿）。</summary>
public sealed class OutlookMeetingDraftTool(IOutlookClient client) : OutlookToolBase<OutlookMeetingDraftArgs>(client)
{
    public override ToolDescriptor Descriptor { get; } = new(
        "outlook_meeting_draft", "建立會議邀請草稿",
        "建立會議邀請草稿，在 Outlook 打開邀請讓使用者檢查後自己按「傳送」。不會寄出，也不會直接進別人的行事曆。" +
        "required 至少一人。沒有與會者的個人行程請用 outlook_calendar_save。" +
        "start、end 用 yyyy-MM-ddTHH:mm 比較清楚。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(40),
        Group: ToolGroups.Outlook, Summary: "建立會議邀請草稿，在 Outlook 打開讓你自己寄出。",
        WarnsAfterExternalContent: true, OptInWarning: OutlookToolText.OptInWarning);

    protected override ToolPreview Describe(OutlookMeetingDraftArgs args, ToolContext context)
    {
        if (!TryBuild(args, out var request, out _))
            return ToolPreview.None;

        var lines = new List<string>
        {
            $"時間：{OutlookToolSupport.FormatRange(request.Start, request.End, allDay: false)}",
            $"與會者：{string.Join("、", request.Required)}",
        };
        if (request.Optional.Count > 0)
            lines.Add($"選用與會者：{string.Join("、", request.Optional)}");
        if (request.Location is not null)
            lines.Add($"地點：{request.Location}");

        var warning = OutlookToolSupport.DomainWarning(Client, request.Required.Concat(request.Optional));
        return new ToolPreview(
            request.Subject,
            string.Join("\n", lines),
            Warnings: warning is null ? null : [warning]);
    }

    protected override async Task<ToolResult> RunAsync(OutlookMeetingDraftArgs args, ToolContext context, CancellationToken ct)
    {
        if (!TryBuild(args, out var request, out var error))
            return ToolResult.Error(error!, "參數不正確");

        await Client.OpenMeetingDraftAsync(request, ct);
        var text = $"已建立會議邀請草稿並在 Outlook 打開，還沒有寄出。與會者：{string.Join("、", request.Required)}；" +
                   $"主旨：{request.Subject}。請使用者檢查後在 Outlook 按「傳送」。";
        return ToolResult.Ok(text, "已建立草稿");
    }

    private static bool TryBuild(OutlookMeetingDraftArgs args, out MeetingDraftRequest request, out string? error)
    {
        request = null!;
        error = null;

        var subject = (args.Subject ?? "").Trim();
        if (subject.Length == 0)
        {
            error = "會議要有主旨。";
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
        if (end <= start)
        {
            error = "結束時間要晚於開始時間。";
            return false;
        }

        var required = OutlookToolSupport.Clean(args.Required);
        if (required.Count == 0)
        {
            error = "至少要有一位與會者（required）。";
            return false;
        }
        if (args.Body is { Length: > OutlookToolSupport.MaxBodyChars })
        {
            error = "內容（body）最多 20,000 字。";
            return false;
        }

        request = new MeetingDraftRequest(
            subject,
            start,
            end,
            required,
            OutlookToolSupport.Clean(args.Optional),
            string.IsNullOrWhiteSpace(args.Location) ? null : args.Location.Trim(),
            string.IsNullOrWhiteSpace(args.Body) ? null : args.Body);
        return true;
    }
}
