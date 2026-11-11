namespace Lumi.Core.Outlook;

public enum OutlookFolder { Inbox, Sent, Drafts, Archive }

/// <param name="Since">本機時間，包含；null＝不限。</param>
/// <param name="Until">本機時間，不包含；null＝不限。</param>
public sealed record MailSearchRequest(string? Query, string? From, OutlookFolder Folder, DateTime? Since, DateTime? Until,
    bool UnreadOnly, bool HasAttachments, int MaxResults);

/// <param name="Id">短代號（m1…）：每則對話各自編號，重開程式後失效。</param>
public sealed record MailSummary(string Id, DateTime Received, string FromName, string FromAddress, string Subject,
    int AttachmentCount, bool IsUnread);

/// <param name="Truncated">本來還有更多符合的信，只回了 MaxResults 封。</param>
public sealed record MailSearchResult(IReadOnlyList<MailSummary> Items, bool Truncated);

public sealed record MailAttachmentInfo(int Index, string FileName, long SizeBytes);

/// <param name="Body">純文字內文（Outlook 的 Body），還沒截斷。</param>
public sealed record MailMessage(MailSummary Summary, IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Body,
    IReadOnlyList<MailAttachmentInfo> Attachments);

public enum DraftKind { New, Reply, ReplyAll, Forward }

/// <param name="Attachments">已經解析好的完整路徑；client 不再檢查範圍。</param>
public sealed record MailDraftRequest(DraftKind Kind, string? SourceId, IReadOnlyList<string> To, IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc, string? Subject, string Body, IReadOnlyList<string> Attachments);

/// <param name="To">Outlook 解析後的收件人（名稱 &lt;地址&gt;），給結果文字用。</param>
public sealed record MailDraftResult(string Subject, IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc);

/// <param name="Id">短代號（e1…）。</param>
/// <param name="IsMeeting">有與會者（不論誰發起）。</param>
public sealed record CalendarEvent(string Id, string Subject, DateTime Start, DateTime End, bool AllDay, string? Location,
    string Organizer, bool IsMeeting, bool IsRecurring, string? ResponseStatus);

public sealed record CalendarQuery(DateTime Start, DateTime End, string? Query);

public sealed record CalendarSaveRequest(string? Id, string Subject, DateTime Start, DateTime End, bool AllDay,
    string? Location, string? Body, int? ReminderMinutes);

public sealed record MeetingDraftRequest(string Subject, DateTime Start, DateTime End, IReadOnlyList<string> Required,
    IReadOnlyList<string> Optional, string? Location, string? Body);

public enum OutlookState { NotSupported, NotInstalled, NotRunning, Available }

/// <param name="Message">給使用者與模型看的白話原因；Available 時是 null。</param>
public sealed record OutlookStatus(OutlookState State, string? Message);

/// <summary>Outlook 呼叫失敗（逾時、Outlook 跳出視窗、權限等級不同、找不到代號…）。訊息是白話繁中，可以直接回給模型。</summary>
public sealed class OutlookException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 使用者已登入的 classic Outlook。實作在 Lumi.Outlook（COM），Mac 與測試用 <see cref="NullOutlookClient"/>。
/// 失敗一律丟 <see cref="OutlookException"/>；按停止（ct 取消）丟 OperationCanceledException。
/// </summary>
public interface IOutlookClient
{
    /// <summary>不連線、不卡住：只看平台、ProgID 與 Outlook 有沒有在執行。</summary>
    OutlookStatus GetStatus();

    /// <summary>自己帳號的 SMTP 地址；還沒連線過或拿不到時是 null（不會為了它去連線）。</summary>
    string? CachedOwnAddress { get; }

    Task<MailSearchResult> SearchMailAsync(string conversationId, MailSearchRequest request, CancellationToken ct);
    Task<MailMessage> ReadMailAsync(string conversationId, string id, CancellationToken ct);
    Task<MailAttachmentInfo> SaveAttachmentAsync(string conversationId, string id, int index, string targetPath, CancellationToken ct);
    Task<MailDraftResult> CreateDraftAsync(string conversationId, MailDraftRequest request, CancellationToken ct);
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string conversationId, CalendarQuery query, CancellationToken ct);
    Task<CalendarEvent> SaveEventAsync(string conversationId, CalendarSaveRequest request, CancellationToken ct);
    Task OpenMeetingDraftAsync(MeetingDraftRequest request, CancellationToken ct);

    /// <summary>
    /// 寄出。request 的格式和草稿相同，但 To／Cc／Bcc 都是電子郵件地址（工具已經檢查過），
    /// 而且回覆、轉寄時會先清掉 Outlook 自動帶的收件人，只寄給 request 列的人。Kind 不會是 ReplyAll。
    /// </summary>
    Task<MailDraftResult> SendMailAsync(string conversationId, MailDraftRequest request, CancellationToken ct);

    /// <summary>
    /// 寄給自己。收件人由實作在寄出當下從 Outlook 讀自己的 SMTP 地址；讀不到就丟 <see cref="OutlookException"/>、不寄。
    /// 主旨由實作加上 <see cref="OutlookAddresses.SelfSubjectPrefix"/>。回傳實際寄到的地址。
    /// </summary>
    /// <param name="attachments">已經解析好的完整路徑。</param>
    Task<string> SendToSelfAsync(string subject, string body, IReadOnlyList<string> attachments, CancellationToken ct);
}
