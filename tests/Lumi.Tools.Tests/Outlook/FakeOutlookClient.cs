using Lumi.Core.Outlook;

namespace Lumi.Tools.Tests.Outlook;

/// <summary>
/// 假的 <see cref="IOutlookClient"/>：可以設定狀態、自己的地址、每個方法的回傳值，或讓它丟 <see cref="OutlookException"/>；
/// 會記錄收到的呼叫（<see cref="GetStatus"/> 與 <see cref="CachedOwnAddress"/> 不算呼叫）。
/// </summary>
internal sealed class FakeOutlookClient : IOutlookClient
{
    public OutlookStatus Status { get; set; } = new(OutlookState.Available, null);

    public string? OwnAddress { get; set; }

    /// <summary>設定後，所有非同步方法都丟這個例外。</summary>
    public Exception? Throw { get; set; }

    public MailSearchResult Search { get; set; } = new([], false);

    public MailMessage? Mail { get; set; }

    public IReadOnlyList<CalendarEvent> Events { get; set; } = [];

    /// <summary>儲存行程的回傳值；沒設定就回傳請求的內容。</summary>
    public CalendarEvent? SavedEvent { get; set; }

    /// <summary>所有實際呼叫的紀錄（依方法名稱）。</summary>
    public List<string> Calls { get; } = [];

    public List<MailSearchRequest> SearchRequests { get; } = [];

    public List<string> SaveTargets { get; } = [];

    public List<MailDraftRequest> Drafts { get; } = [];

    public List<MailDraftRequest> Sends { get; } = [];

    public List<(string Subject, string Body, IReadOnlyList<string> Attachments)> SelfMails { get; } = [];

    public List<CalendarQuery> CalendarQueries { get; } = [];

    public List<CalendarSaveRequest> EventSaves { get; } = [];

    public List<MeetingDraftRequest> MeetingDrafts { get; } = [];

    public OutlookStatus GetStatus() => Status;

    public string? CachedOwnAddress => OwnAddress;

    private void Fail()
    {
        if (Throw is not null)
            throw Throw;
    }

    public async Task<MailSearchResult> SearchMailAsync(string conversationId, MailSearchRequest request, CancellationToken ct)
    {
        Calls.Add("SearchMail");
        SearchRequests.Add(request);
        Fail();
        return Search;
    }

    public async Task<MailMessage> ReadMailAsync(string conversationId, string id, CancellationToken ct)
    {
        Calls.Add("ReadMail");
        Fail();
        return Mail ?? throw new OutlookException("找不到這個代號，請重新搜尋。");
    }

    public async Task<MailAttachmentInfo> SaveAttachmentAsync(string conversationId, string id, int index, string targetPath, CancellationToken ct)
    {
        Calls.Add("SaveAttachment");
        SaveTargets.Add(targetPath);
        Fail();
        return new MailAttachmentInfo(index, Path.GetFileName(targetPath), 5);
    }

    public async Task<MailDraftResult> CreateDraftAsync(string conversationId, MailDraftRequest request, CancellationToken ct)
    {
        Calls.Add("CreateDraft");
        Drafts.Add(request);
        Fail();
        return new MailDraftResult(request.Subject ?? "", request.To, request.Cc, request.Bcc);
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string conversationId, CalendarQuery query, CancellationToken ct)
    {
        Calls.Add("ListEvents");
        CalendarQueries.Add(query);
        Fail();
        return Events;
    }

    public async Task<CalendarEvent> SaveEventAsync(string conversationId, CalendarSaveRequest request, CancellationToken ct)
    {
        Calls.Add("SaveEvent");
        EventSaves.Add(request);
        Fail();
        return SavedEvent ?? new CalendarEvent(request.Id ?? "e1", request.Subject, request.Start, request.End, request.AllDay,
            request.Location, "我", false, false, null);
    }

    public async Task OpenMeetingDraftAsync(MeetingDraftRequest request, CancellationToken ct)
    {
        Calls.Add("OpenMeetingDraft");
        MeetingDrafts.Add(request);
        Fail();
    }

    public async Task<MailDraftResult> SendMailAsync(string conversationId, MailDraftRequest request, CancellationToken ct)
    {
        Calls.Add("SendMail");
        Sends.Add(request);
        Fail();
        return new MailDraftResult(request.Subject ?? "", request.To, request.Cc, request.Bcc);
    }

    public async Task<string> SendToSelfAsync(string subject, string body, IReadOnlyList<string> attachments, CancellationToken ct)
    {
        Calls.Add("SendToSelf");
        SelfMails.Add((subject, body, attachments));
        Fail();
        return OwnAddress ?? "me@corp.com";
    }
}
