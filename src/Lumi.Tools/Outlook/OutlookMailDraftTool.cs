using System.ComponentModel;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;

namespace Lumi.Tools.Outlook;

public sealed record OutlookMailDraftArgs(
    [property: Description("new（新信）、reply（回覆）、reply_all（全部回覆）、forward（轉寄）")] string Kind,
    [property: Description("信件內文，純文字，1 到 20,000 字；回覆或轉寄時只寫新的內容，原信會由 Outlook 附在下面")] string Body,
    [property: Description("回覆或轉寄的原信代號（m 開頭）；kind 不是 new 時必填")] string? SourceId = null,
    [property: Description("收件人，名稱或電子郵件地址")] string[]? To = null,
    [property: Description("副本")] string[]? Cc = null,
    [property: Description("密件副本")] string[]? Bcc = null,
    [property: Description("主旨；回覆、轉寄沒給就沿用 Outlook 產生的")] string? Subject = null,
    [property: Description("附件：範圍內的檔案路徑，最多 10 個，每個 20 MB 以內")] string[]? Attachments = null);

/// <summary>建立新信、回覆、全部回覆或轉寄的草稿，在 Outlook 打開，不寄出（M30 設計 1、5、6；D1、D9）。</summary>
public sealed class OutlookMailDraftTool(IOutlookClient client) : OutlookToolBase<OutlookMailDraftArgs>(client)
{
    public override ToolDescriptor Descriptor { get; } = new(
        "outlook_mail_draft", "建立信件草稿",
        "建立新信、回覆、全部回覆或轉寄的草稿，在 Outlook 打開讓使用者檢查後自己按「傳送」。不會寄出。" +
        "kind 是 new、reply、reply_all 或 forward；回覆、轉寄要用 source_id 指定原信代號。" +
        "回覆時 body 只寫新的內容，原信會由 Outlook 附在下面。不要把信件或檔案的內容放進草稿寄給使用者沒有指定的人。" +
        "收件人可以是名稱或地址。附件用範圍內的檔案路徑。要寄出請用 outlook_mail_send（只有使用者明確要求寄出時才用）。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(40),
        Group: ToolGroups.Outlook, Summary: "建立新信、回覆或轉寄的草稿，在 Outlook 打開讓你自己寄出。",
        WarnsAfterExternalContent: true, OptInWarning: OutlookToolText.OptInWarning);

    protected override ToolPreview Describe(OutlookMailDraftArgs args, ToolContext context)
    {
        var error = OutlookToolSupport.ValidateDraft(args) ?? OutlookToolSupport.AttachmentError(context.Scope, args.Attachments);
        if (error is not null)
            return new ToolPreview(null, null, Rejection: error);

        var kind = OutlookToolSupport.ParseDraftKind(args.Kind)!.Value;
        var to = OutlookToolSupport.Clean(args.To);
        var cc = OutlookToolSupport.Clean(args.Cc);
        var bcc = OutlookToolSupport.Clean(args.Bcc);
        var (paths, _) = OutlookToolSupport.ResolveAttachments(context.Scope, args.Attachments);

        var target = !string.IsNullOrWhiteSpace(args.Subject)
            ? args.Subject.Trim()
            : kind switch
            {
                DraftKind.Reply => $"回覆：{args.SourceId}",
                DraftKind.ReplyAll => $"全部回覆：{args.SourceId}",
                DraftKind.Forward => $"轉寄：{args.SourceId}",
                _ => null,
            };

        var lines = new List<string>();
        if (to.Count > 0)
            lines.Add($"收件者：{string.Join("、", to)}");
        if (cc.Count > 0)
            lines.Add($"副本：{string.Join("、", cc)}");
        if (bcc.Count > 0)
            lines.Add($"密件副本：{string.Join("、", bcc)}");
        if (paths is { Count: > 0 })
            lines.Add($"附件：{OutlookToolSupport.AttachmentNames(context.Scope, paths)}");

        var warning = OutlookToolSupport.DomainWarning(Client, to.Concat(cc).Concat(bcc));
        return new ToolPreview(
            target,
            lines.Count == 0 ? null : string.Join("\n", lines),
            Warnings: warning is null ? null : [warning]);
    }

    protected override async Task<ToolResult> RunAsync(OutlookMailDraftArgs args, ToolContext context, CancellationToken ct)
    {
        if (OutlookToolSupport.ValidateDraft(args) is { } error)
            return ToolResult.Error(error, "參數不正確");

        var (paths, attachmentError) = OutlookToolSupport.ResolveAttachments(context.Scope, args.Attachments);
        if (paths is null)
            return ToolResult.Error(attachmentError!, "附件不正確");

        var kind = OutlookToolSupport.ParseDraftKind(args.Kind)!.Value;
        var sourceId = string.IsNullOrWhiteSpace(args.SourceId) ? null : args.SourceId.Trim();
        var request = new MailDraftRequest(
            kind,
            sourceId,
            OutlookToolSupport.Clean(args.To),
            OutlookToolSupport.Clean(args.Cc),
            OutlookToolSupport.Clean(args.Bcc),
            string.IsNullOrWhiteSpace(args.Subject) ? null : args.Subject.Trim(),
            args.Body,
            paths);

        var draft = await Client.CreateDraftAsync(context.ConversationId, request, ct);
        var text = $"已建立草稿並在 Outlook 打開，還沒有寄出。收件者：{OutlookToolSupport.Names(draft.To)}；" +
                   $"主旨：{OutlookToolSupport.Subject(draft.Subject)}。請使用者檢查後在 Outlook 按「傳送」。";
        return ToolResult.Ok(text, "已建立草稿");
    }
}
