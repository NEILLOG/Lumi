using System.Globalization;
using Lumi.Core.Outlook;
using Interop = Microsoft.Office.Interop.Outlook;

namespace Lumi.Outlook;

/// <summary>行事曆：查行程、新增或修改自己的行程、會議邀請草稿。</summary>
internal sealed partial class OutlookClient
{
    private const int MaxCalendarEvents = 200;

    public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string conversationId, CalendarQuery query, CancellationToken ct) =>
        CallAsync(nameof(ListEventsAsync), DefaultTimeout, ct, call => ListEvents(call, conversationId, query));

    public Task<CalendarEvent> SaveEventAsync(string conversationId, CalendarSaveRequest request, CancellationToken ct) =>
        CallAsync(nameof(SaveEventAsync), DefaultTimeout, ct, call => SaveEvent(call, conversationId, request));

    public async Task OpenMeetingDraftAsync(MeetingDraftRequest request, CancellationToken ct)
    {
        await CallAsync(nameof(OpenMeetingDraftAsync), DefaultTimeout, ct, call =>
        {
            OpenMeetingDraft(call, request);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>預設行事曆的展開後行程（含週期性），最多 200 筆。</summary>
    private IReadOnlyList<CalendarEvent> ListEvents(OutlookCall call, string conversationId, CalendarQuery query)
    {
        var calendar = call.Scope.Track(call.NameSpace.GetDefaultFolder(Interop.OlDefaultFolders.olFolderCalendar));
        var storeId = calendar.StoreID;
        var all = call.Scope.Track(calendar.Items);
        all.Sort("[Start]", false);
        all.IncludeRecurrences = true;
        var items = call.Scope.Track((Interop.Items)all.Restrict(
            OutlookFilters.BuildCalendarRestrict(query.Start, query.End, CultureInfo.CurrentCulture)));

        // 不能用 Count：週期性展開後 Count 不準，所以用 GetFirst／GetNext 走訪
        var events = new List<CalendarEvent>();
        object? raw = items.GetFirst();
        while (raw is not null && events.Count < MaxCalendarEvents)
        {
            using var itemScope = new ComScope();
            itemScope.Track(raw);

            if (raw is Interop.AppointmentItem appointment && Overlaps(appointment, query) && Matches(appointment, query.Query))
                events.Add(EventOf(call, conversationId, appointment, storeId));

            raw = items.GetNext();
        }
        return events;
    }

    /// <summary>
    /// 新增（沒有 Id）或修改自己的行程。有與會者的行程不在這裡改。
    /// 行程只保存，不發出邀請。
    /// </summary>
    private CalendarEvent SaveEvent(OutlookCall call, string conversationId, CalendarSaveRequest request)
    {
        Interop.AppointmentItem appointment;
        string storeId;
        var isNew = request.Id is null;
        if (request.Id is not null)
        {
            appointment = GetAppointment(call, conversationId, request.Id, out storeId);
            if (appointment.MeetingStatus != Interop.OlMeetingStatus.olNonMeeting
                || call.Scope.Track(appointment.Recipients).Count > 0)
                throw new OutlookException("這個行程有與會者，請在 Outlook 裡修改。");
        }
        else
        {
            storeId = call.Scope.Track(call.NameSpace.GetDefaultFolder(Interop.OlDefaultFolders.olFolderCalendar)).StoreID;
            appointment = CreateAppointment(call);
        }

        try
        {
            // 先設全天：Outlook 會依全天與否調整 Start／End
            appointment.AllDayEvent = request.AllDay;
            appointment.Subject = request.Subject;
            appointment.Start = request.Start;
            appointment.End = request.End;
            if (request.Location is not null)
                appointment.Location = request.Location;
            if (request.Body is not null)
                appointment.Body = request.Body;
            if (request.ReminderMinutes is { } minutes)
            {
                appointment.ReminderSet = true;
                appointment.ReminderMinutesBeforeStart = minutes;
            }
            appointment.Save();
        }
        catch
        {
            if (isNew)
                Discard(appointment);
            throw;
        }

        return EventOf(call, conversationId, appointment, storeId);
    }

    /// <summary>會議邀請只建成草稿：不 Save、不 Send，由使用者自己決定要不要送出（D15）。</summary>
    private void OpenMeetingDraft(OutlookCall call, MeetingDraftRequest request)
    {
        var appointment = CreateAppointment(call);
        try
        {
            appointment.MeetingStatus = Interop.OlMeetingStatus.olMeeting;
            appointment.Subject = request.Subject;
            appointment.Start = request.Start;
            appointment.End = request.End;
            if (request.Location is not null)
                appointment.Location = request.Location;
            if (request.Body is not null)
                appointment.Body = request.Body;

            var recipients = call.Scope.Track(appointment.Recipients);
            var added = new List<(string Text, Interop.Recipient Recipient)>();
            AddRecipients(call, recipients, request.Required, (int)Interop.OlMeetingRecipientType.olRequired, added);
            AddRecipients(call, recipients, request.Optional, (int)Interop.OlMeetingRecipientType.olOptional, added);
            EnsureResolved(recipients, added);
        }
        catch
        {
            Discard(appointment);
            throw;
        }

        appointment.Display(false);
    }

    private static Interop.AppointmentItem CreateAppointment(OutlookCall call)
    {
        var raw = call.Scope.Track(call.App.CreateItem(Interop.OlItemType.olAppointmentItem));
        return raw as Interop.AppointmentItem ?? throw new OutlookException(CreateFailed);
    }

    private Interop.AppointmentItem GetAppointment(OutlookCall call, string conversationId, string id, out string storeId) =>
        GetItemByHandle(call, conversationId, id, out storeId) as Interop.AppointmentItem ?? throw new OutlookException(HandleExpired);

    private static bool Overlaps(Interop.AppointmentItem appointment, CalendarQuery query) =>
        appointment.Start < query.End && appointment.End > query.Start;

    private static bool Matches(Interop.AppointmentItem appointment, string? text) =>
        string.IsNullOrWhiteSpace(text)
        || (appointment.Subject ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)
        || (appointment.Location ?? "").Contains(text, StringComparison.OrdinalIgnoreCase);

    private CalendarEvent EventOf(OutlookCall call, string conversationId, Interop.AppointmentItem appointment, string storeId)
    {
        var id = _handles.Add(conversationId, 'e', appointment.EntryID, storeId);
        var recipients = call.Scope.Track(appointment.Recipients);
        return new CalendarEvent(
            id,
            appointment.Subject ?? "",
            appointment.Start,
            appointment.End,
            appointment.AllDayEvent,
            string.IsNullOrEmpty(appointment.Location) ? null : appointment.Location,
            appointment.Organizer ?? "",
            recipients.Count > 0,
            appointment.IsRecurring,
            ResponseText((int)appointment.ResponseStatus));
    }

    private static string? ResponseText(int status) => status switch
    {
        (int)Interop.OlResponseStatus.olResponseOrganized => "我發起",
        (int)Interop.OlResponseStatus.olResponseTentative => "暫定",
        (int)Interop.OlResponseStatus.olResponseAccepted => "已接受",
        (int)Interop.OlResponseStatus.olResponseDeclined => "已婉拒",
        (int)Interop.OlResponseStatus.olResponseNotResponded => "尚未回覆",
        _ => null,
    };
}
