namespace Lumi.Core.Outlook;

/// <summary>沒有 Outlook 可用時的預設（Mac、測試）；Windows 上由 Lumi.Outlook 的 <c>AddLumiOutlook</c> 取代。</summary>
public sealed class NullOutlookClient : IOutlookClient
{
    public const string Message = "Outlook 工具只能在 Windows 的 classic Outlook 使用。";

    public OutlookStatus GetStatus() => new(OutlookState.NotSupported, Message);

    public string? CachedOwnAddress => null;

    public Task<MailSearchResult> SearchMailAsync(string conversationId, MailSearchRequest request, CancellationToken ct) => Fail<MailSearchResult>();
    public Task<MailMessage> ReadMailAsync(string conversationId, string id, CancellationToken ct) => Fail<MailMessage>();
    public Task<MailAttachmentInfo> SaveAttachmentAsync(string conversationId, string id, int index, string targetPath, CancellationToken ct) => Fail<MailAttachmentInfo>();
    public Task<MailDraftResult> CreateDraftAsync(string conversationId, MailDraftRequest request, CancellationToken ct) => Fail<MailDraftResult>();
    public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string conversationId, CalendarQuery query, CancellationToken ct) => Fail<IReadOnlyList<CalendarEvent>>();
    public Task<CalendarEvent> SaveEventAsync(string conversationId, CalendarSaveRequest request, CancellationToken ct) => Fail<CalendarEvent>();
    public Task OpenMeetingDraftAsync(MeetingDraftRequest request, CancellationToken ct) => Fail<bool>();
    public Task<MailDraftResult> SendMailAsync(string conversationId, MailDraftRequest request, CancellationToken ct) => Fail<MailDraftResult>();
    public Task<string> SendToSelfAsync(string subject, string body, IReadOnlyList<string> attachments, CancellationToken ct) => Fail<string>();

    private static Task<T> Fail<T>() => Task.FromException<T>(new OutlookException(Message));
}
