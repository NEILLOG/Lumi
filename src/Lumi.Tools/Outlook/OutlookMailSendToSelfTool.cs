using System.ComponentModel;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;

namespace Lumi.Tools.Outlook;

// 注意：這個參數型別刻意不含任何收件人欄位（D13）。收件人由 client 在寄出當下從 Outlook 讀自己的地址。
public sealed record OutlookMailSendToSelfArgs(
    [property: Description("主旨，1 到 200 字；會自動在前面加上「[Lumi] 」")] string Subject,
    [property: Description("信件內文，純文字，1 到 20,000 字")] string Body,
    [property: Description("附件：範圍內的檔案路徑，最多 10 個，每個 20 MB 以內")] string[]? Attachments = null);

/// <summary>把一封信寄到使用者自己的信箱（D13）。權限 Write，不設外部內容警告，排程可以用寫入授權自動寄。</summary>
public sealed class OutlookMailSendToSelfTool(IOutlookClient client) : OutlookToolBase<OutlookMailSendToSelfArgs>(client)
{
    public override ToolDescriptor Descriptor { get; } = new(
        "outlook_mail_send_to_self", "寄信給自己",
        "把一封信寄到使用者自己的信箱。收件人固定是使用者自己，沒有收件人參數，不能指定別人。" +
        "用在使用者要你把結果、摘要或提醒寄給他自己，或排程要寄報告的時候。主旨會自動加上「[Lumi] 」。" +
        "附件用範圍內的檔案路徑，最多 10 個，每個 20 MB 以內。要寄給別人請用 outlook_mail_draft 建草稿。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(40),
        Group: ToolGroups.Outlook, Summary: "把內容寄到你自己的信箱。",
        OptInWarning: OutlookToolText.OptInWarning);

    protected override ToolPreview Describe(OutlookMailSendToSelfArgs args, ToolContext context)
    {
        var error = OutlookToolSupport.ValidateSelf(args) ?? OutlookToolSupport.AttachmentError(context.Scope, args.Attachments);
        if (error is not null)
            return new ToolPreview(null, null, Rejection: error);

        var (paths, _) = OutlookToolSupport.ResolveAttachments(context.Scope, args.Attachments);
        var detail = "寄到你自己的信箱";
        if (paths is { Count: > 0 })
            detail += $"\n附件：{OutlookToolSupport.AttachmentNames(context.Scope, paths)}";

        return new ToolPreview(OutlookAddresses.SelfSubjectPrefix + args.Subject.Trim(), detail);
    }

    protected override async Task<ToolResult> RunAsync(OutlookMailSendToSelfArgs args, ToolContext context, CancellationToken ct)
    {
        if (OutlookToolSupport.ValidateSelf(args) is { } error)
            return ToolResult.Error(error, "參數不正確");

        var (paths, attachmentError) = OutlookToolSupport.ResolveAttachments(context.Scope, args.Attachments);
        if (paths is null)
            return ToolResult.Error(attachmentError!, "附件不正確");

        // 主旨前綴由 client 加，這裡不加
        var subject = args.Subject.Trim();
        var address = await Client.SendToSelfAsync(subject, args.Body, paths, ct);
        return ToolResult.Ok(
            $"已寄到使用者自己的信箱（{address}）。主旨：{OutlookAddresses.SelfSubjectPrefix}{subject}",
            "已寄給自己");
    }

}
