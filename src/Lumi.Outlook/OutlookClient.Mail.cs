using System.Globalization;
using Lumi.Core.Outlook;
using Interop = Microsoft.Office.Interop.Outlook;

namespace Lumi.Outlook;

/// <summary>信件：搜尋、讀信、存附件、草稿、寄出、寄給自己。</summary>
internal sealed partial class OutlookClient
{
    public Task<MailSearchResult> SearchMailAsync(string conversationId, MailSearchRequest request, CancellationToken ct) =>
        CallAsync(nameof(SearchMailAsync), SearchTimeout, ct, call => SearchMail(call, conversationId, request));

    public Task<MailMessage> ReadMailAsync(string conversationId, string id, CancellationToken ct) =>
        CallAsync(nameof(ReadMailAsync), DefaultTimeout, ct, call => ReadMail(call, conversationId, id));

    public Task<MailAttachmentInfo> SaveAttachmentAsync(string conversationId, string id, int index, string targetPath, CancellationToken ct) =>
        CallAsync(nameof(SaveAttachmentAsync), DefaultTimeout, ct, call => SaveAttachment(call, conversationId, id, index, targetPath));

    public Task<MailDraftResult> CreateDraftAsync(string conversationId, MailDraftRequest request, CancellationToken ct) =>
        CallAsync(nameof(CreateDraftAsync), DefaultTimeout, ct, call => CreateDraft(call, conversationId, request));

    public Task<MailDraftResult> SendMailAsync(string conversationId, MailDraftRequest request, CancellationToken ct) =>
        CallAsync(nameof(SendMailAsync), DefaultTimeout, ct, call => SendMail(call, conversationId, request));

    public Task<string> SendToSelfAsync(string subject, string body, IReadOnlyList<string> attachments, CancellationToken ct) =>
        CallAsync(nameof(SendToSelfAsync), DefaultTimeout, ct, call => SendToSelf(call, subject, body, attachments));

    private MailSearchResult SearchMail(OutlookCall call, string conversationId, MailSearchRequest request)
    {
        var folder = call.Scope.Track(GetFolder(call, request.Folder));
        var storeId = folder.StoreID;
        var items = call.Scope.Track(folder.Items);

        // 文字條件用 DASL 篩選；日期不放進篩選（D7），下面逐封檢查
        var filter = OutlookFilters.BuildMailFilter(request);
        if (filter is not null)
            items = call.Scope.Track((Interop.Items)items.Restrict(filter));
        items.Sort(SortField(request.Folder), true);

        var summaries = new List<MailSummary>();
        var truncated = false;
        object? raw = items.GetFirst();
        while (raw is not null)
        {
            using var itemScope = new ComScope();
            itemScope.Track(raw);

            // 只看信件（會議通知、回條略過）
            if (raw is Interop.MailItem mail)
            {
                var received = MailTime(mail);

                // 由新到舊：比 since 舊就可以停了
                if (OutlookFilters.IsOlderThanSince(received, request.Since))
                    break;

                if (OutlookFilters.InRange(received, request.Since, request.Until))
                {
                    if (summaries.Count >= request.MaxResults)
                    {
                        truncated = true;
                        break;
                    }
                    summaries.Add(Summarize(call, conversationId, mail, received, storeId));
                }
            }

            raw = items.GetNext();
        }

        return new MailSearchResult(summaries, truncated);
    }

    private MailMessage ReadMail(OutlookCall call, string conversationId, string id)
    {
        var mail = GetMail(call, conversationId, id, out var storeId);
        var summary = Summarize(call, conversationId, mail, MailTime(mail), storeId);

        var attachments = ByValueAttachments(call, mail)
            .Select((attachment, i) => new MailAttachmentInfo(i + 1, attachment.FileName ?? "", attachment.Size))
            .ToList();

        return new MailMessage(
            summary,
            RecipientTexts(call, mail, Interop.OlMailRecipientType.olTo),
            RecipientTexts(call, mail, Interop.OlMailRecipientType.olCC),
            mail.Body ?? "",
            attachments);
    }

    private MailAttachmentInfo SaveAttachment(OutlookCall call, string conversationId, string id, int index, string targetPath)
    {
        var mail = GetMail(call, conversationId, id, out _);
        var attachments = ByValueAttachments(call, mail);
        if (index < 1 || index > attachments.Count)
            throw new OutlookException("找不到這個附件，請重新讀信後再試。");

        var attachment = attachments[index - 1];
        attachment.SaveAsFile(targetPath);
        return new MailAttachmentInfo(index, attachment.FileName ?? "", attachment.Size);
    }

    /// <summary>建成草稿，在 Outlook 打開，不寄出（D1）。</summary>
    private MailDraftResult CreateDraft(OutlookCall call, string conversationId, MailDraftRequest request)
    {
        var item = BuildMail(call, conversationId, request, isSend: false);
        item.Save();
        item.Display(false);
        return ResultOf(call, item);
    }

    /// <summary>
    /// 寄出（D14）。Send() 只能從 SendMailAsync 這條路徑呼叫。
    /// </summary>
    private MailDraftResult SendMail(OutlookCall call, string conversationId, MailDraftRequest request)
    {
        if (request.Kind == DraftKind.ReplyAll)
            throw new OutlookException("這種寄法不支援全部回覆，請改用回覆並列出收件人。");

        var item = BuildMail(call, conversationId, request, isSend: true);
        try
        {
            // 實際收件人必須剛好是要求的那些（使用者在確認卡上看到的）；對不上就不寄
            var expected = request.To.Count + request.Cc.Count + request.Bcc.Count;
            var recipients = call.Scope.Track(item.Recipients);
            if (expected == 0 || recipients.Count != expected)
                throw new OutlookException("收件人和確認的內容對不上，沒有寄出。");

            // Send 之後這個物件就不能再用，所以先記下主旨與實際收件人
            var result = ResultOf(call, item);
            item.Send();
            return result;
        }
        catch
        {
            Discard(item);
            throw;
        }
    }

    /// <summary>
    /// 寄給自己（D13）。收件人在寄出當下重新從 Outlook 讀取，不用快取。Send() 只能從 SendToSelfAsync 這條路徑呼叫。
    /// </summary>
    private string SendToSelf(OutlookCall call, string subject, string body, IReadOnlyList<string> attachments)
    {
        var address = ReadOwnAddress(call) ?? throw new OutlookException(NoOwnAddress);

        var item = CreateMailItem(call);
        try
        {
            var recipients = call.Scope.Track(item.Recipients);
            AddRecipients(call, recipients, [address], (int)Interop.OlMailRecipientType.olTo, []);
            if (!recipients.ResolveAll())
                throw new OutlookException(NoOwnAddress);

            item.Subject = OutlookAddresses.SelfSubjectPrefix + subject;
            item.BodyFormat = Interop.OlBodyFormat.olFormatPlain;
            item.Body = body;
            AddAttachments(call, item, attachments);

            // 收件人只能有自己一個
            if (recipients.Count != 1)
                throw new OutlookException(NoOwnAddress);
            item.Send();
        }
        catch
        {
            Discard(item);
            throw;
        }

        return address;
    }

    /// <summary>
    /// 建立草稿或寄件的項目：新信、回覆、轉寄，設定收件人、主旨、內文與附件。
    /// 任何一步失敗都丟掉項目（不保存、不寄出）。isSend 時先清掉 Outlook 自動帶的收件人（D14）。
    /// </summary>
    private Interop.MailItem BuildMail(OutlookCall call, string conversationId, MailDraftRequest request, bool isSend)
    {
        var item = request.Kind == DraftKind.New
            ? CreateMailItem(call)
            : CreateReplyItem(call, conversationId, request.Kind, request.SourceId);

        try
        {
            var recipients = call.Scope.Track(item.Recipients);
            if (isSend)
            {
                while (recipients.Count > 0)
                    recipients.Remove(1);
            }

            var added = new List<(string Text, Interop.Recipient Recipient)>();
            AddRecipients(call, recipients, request.To, (int)Interop.OlMailRecipientType.olTo, added);
            AddRecipients(call, recipients, request.Cc, (int)Interop.OlMailRecipientType.olCC, added);
            AddRecipients(call, recipients, request.Bcc, (int)Interop.OlMailRecipientType.olBCC, added);
            EnsureResolved(recipients, added);

            if (request.Subject is not null)
                item.Subject = request.Subject;
            ApplyBody(item, request.Body);
            AddAttachments(call, item, request.Attachments);
            return item;
        }
        catch
        {
            Discard(item);
            throw;
        }
    }

    private static Interop.MailItem CreateMailItem(OutlookCall call)
    {
        var raw = call.Scope.Track(call.App.CreateItem(Interop.OlItemType.olMailItem));
        return raw as Interop.MailItem ?? throw new OutlookException(CreateFailed);
    }

    private Interop.MailItem CreateReplyItem(OutlookCall call, string conversationId, DraftKind kind, string? sourceId)
    {
        if (sourceId is null)
            throw new OutlookException("回覆或轉寄需要來源信件的代號。");

        var source = GetMail(call, conversationId, sourceId, out _);
        object raw = kind switch
        {
            DraftKind.Reply => source.Reply(),
            DraftKind.ReplyAll => source.ReplyAll(),
            _ => source.Forward(),
        };
        return call.Scope.Track(raw) as Interop.MailItem ?? throw new OutlookException(CreateFailed);
    }

    /// <summary>純文字：內文加在前面，中間空一行。HTML：插進 body 標籤之後（保留原信格式）。</summary>
    private static void ApplyBody(Interop.MailItem item, string body)
    {
        if (item.BodyFormat == Interop.OlBodyFormat.olFormatHTML)
        {
            item.HTMLBody = HtmlBodyInserter.Insert(item.HTMLBody ?? "", body);
            return;
        }

        var original = item.Body ?? "";
        item.Body = original.Length == 0 ? body : body + "\r\n\r\n" + original;
    }

    private MailDraftResult ResultOf(OutlookCall call, Interop.MailItem item) =>
        new(
            item.Subject ?? "",
            RecipientTexts(call, item, Interop.OlMailRecipientType.olTo),
            RecipientTexts(call, item, Interop.OlMailRecipientType.olCC),
            RecipientTexts(call, item, Interop.OlMailRecipientType.olBCC));

    private static List<string> RecipientTexts(OutlookCall call, Interop.MailItem mail, Interop.OlMailRecipientType type)
    {
        var recipients = call.Scope.Track(mail.Recipients);
        var result = new List<string>();
        for (var i = 1; i <= recipients.Count; i++)
        {
            var recipient = call.Scope.Track(recipients[i]);
            if (recipient.Type == (int)type)
                result.Add(RecipientText(call, recipient));
        }
        return result;
    }

    /// <summary>附件清單只列 olByValue；序號從 1 開始，對應讀信回傳的 Index。</summary>
    private static List<Interop.Attachment> ByValueAttachments(OutlookCall call, Interop.MailItem mail)
    {
        var attachments = call.Scope.Track(mail.Attachments);
        var result = new List<Interop.Attachment>();
        for (var i = 1; i <= attachments.Count; i++)
        {
            var attachment = call.Scope.Track(attachments[i]);
            if (Convert.ToInt32(attachment.Type, CultureInfo.InvariantCulture) == (int)Interop.OlAttachmentType.olByValue)
                result.Add(attachment);
        }
        return result;
    }

    private MailSummary Summarize(OutlookCall call, string conversationId, Interop.MailItem mail, DateTime received, string storeId)
    {
        var id = _handles.Add(conversationId, 'm', mail.EntryID, storeId);
        return new MailSummary(
            id,
            received,
            mail.SenderName ?? "",
            SenderAddress(call, mail),
            mail.Subject ?? "",
            ByValueAttachments(call, mail).Count,
            mail.UnRead);
    }

    /// <summary>Exchange 寄件人用 SMTP 地址，拿不到就用顯示名稱；其他用寄件人地址。</summary>
    private static string SenderAddress(OutlookCall call, Interop.MailItem mail)
    {
        if (string.Equals(mail.SenderEmailType, "EX", StringComparison.OrdinalIgnoreCase))
        {
            var sender = call.Scope.Track(mail.Sender);
            var user = call.Scope.Track(sender?.GetExchangeUser());
            return user?.PrimarySmtpAddress ?? mail.SenderName ?? "";
        }
        return mail.SenderEmailAddress ?? "";
    }

    private Interop.MailItem GetMail(OutlookCall call, string conversationId, string id, out string storeId) =>
        GetItemByHandle(call, conversationId, id, out storeId) as Interop.MailItem ?? throw new OutlookException(HandleExpired);

    private static Interop.MAPIFolder GetFolder(OutlookCall call, OutlookFolder folder)
    {
        var ns = call.NameSpace;
        return folder switch
        {
            OutlookFolder.Inbox => call.Scope.Track(ns.GetDefaultFolder(Interop.OlDefaultFolders.olFolderInbox)),
            OutlookFolder.Sent => call.Scope.Track(ns.GetDefaultFolder(Interop.OlDefaultFolders.olFolderSentMail)),
            OutlookFolder.Drafts => call.Scope.Track(ns.GetDefaultFolder(Interop.OlDefaultFolders.olFolderDrafts)),
            _ => FindArchive(call) ?? throw new OutlookException("找不到「封存」或「Archive」資料夾。"),
        };
    }

    /// <summary>預設信箱底下名稱是「封存」或「Archive」的資料夾。</summary>
    private static Interop.MAPIFolder? FindArchive(OutlookCall call)
    {
        var inbox = call.Scope.Track(call.NameSpace.GetDefaultFolder(Interop.OlDefaultFolders.olFolderInbox));
        var root = call.Scope.Track(inbox.Parent as Interop.MAPIFolder);
        if (root is null)
            return null;

        var children = call.Scope.Track(root.Folders);
        for (var i = 1; i <= children.Count; i++)
        {
            var child = call.Scope.Track(children[i]);
            if (child.Name == "封存" || string.Equals(child.Name, "Archive", StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    /// <summary>排序欄位：寄件備份用寄出時間，草稿用建立時間，其他用收到時間。</summary>
    private static string SortField(OutlookFolder folder) => folder switch
    {
        OutlookFolder.Sent => "[SentOn]",
        OutlookFolder.Drafts => "[CreationTime]",
        _ => "[ReceivedTime]",
    };

    /// <summary>Outlook 對還沒收到或寄出的信會回傳很遠的年份，依序挑第一個合理的時間。</summary>
    private static DateTime MailTime(Interop.MailItem mail)
    {
        if (IsSane(mail.ReceivedTime))
            return mail.ReceivedTime;
        if (IsSane(mail.SentOn))
            return mail.SentOn;
        return mail.CreationTime;
    }

    private static bool IsSane(DateTime time) => time.Year < 4000;
}
