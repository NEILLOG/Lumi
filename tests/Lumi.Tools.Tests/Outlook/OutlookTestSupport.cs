using Lumi.Core.Chat;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;
using Lumi.Tools.Outlook;
using Lumi.Tools.Tests.Files;

namespace Lumi.Tools.Tests.Outlook;

/// <summary>Outlook 工具測試共用：九個工具的實例與合法引數、執行環境。</summary>
internal static class OutlookTestSupport
{
    /// <summary>九個工具：名稱、建立方式、一組合法引數（用來測「Outlook 沒開」等不需要真的執行的情況）。</summary>
    public static readonly (string Name, Func<IOutlookClient, ITool> Make, string Args)[] All =
    [
        ("outlook_mail_search", c => new OutlookMailSearchTool(c), "{}"),
        ("outlook_mail_read", c => new OutlookMailReadTool(c), "{\"id\":\"m1\"}"),
        ("outlook_calendar_list", c => new OutlookCalendarListTool(c), "{\"start\":\"2026-11-10\"}"),
        ("outlook_mail_save_attachment", c => new OutlookMailSaveAttachmentTool(c), "{\"id\":\"m1\",\"attachment\":1,\"folder\":\"下載\"}"),
        ("outlook_mail_draft", c => new OutlookMailDraftTool(c), "{\"kind\":\"new\",\"body\":\"內容\"}"),
        ("outlook_calendar_save", c => new OutlookCalendarSaveTool(c), "{\"subject\":\"週會\",\"start\":\"2026-11-10T09:00\",\"end\":\"2026-11-10T10:00\"}"),
        ("outlook_meeting_draft", c => new OutlookMeetingDraftTool(c), "{\"subject\":\"會議\",\"start\":\"2026-11-10T09:00\",\"end\":\"2026-11-10T10:00\",\"required\":[\"a@corp.com\"]}"),
        ("outlook_mail_send_to_self", c => new OutlookMailSendToSelfTool(c), "{\"subject\":\"摘要\",\"body\":\"內容\"}"),
        ("outlook_mail_send", c => new OutlookMailSendTool(c), "{\"kind\":\"new\",\"to\":[\"a@corp.com\"],\"subject\":\"主旨\",\"body\":\"內容\"}"),
    ];

    public static ITool Make(string name, IOutlookClient client) =>
        OutlookTestSupport.All.Single(t => t.Name == name).Make(client);

    public static ToolContext Context(IFileScope? scope = null, ChatMode mode = ChatMode.Workbench) =>
        new("c1", mode, TimeProvider.System, scope ?? EmptyFileScope.Instance);

    public static IFileScope ScopeOf(TempTree tree) => new FileScope([new ScopeRoot("專案", tree.Root, true)]);

    public static MailSummary Mail(string id, string address, string subject, int attachments = 0, bool unread = false) =>
        new(id, new DateTime(2026, 11, 9, 14, 32, 0), "王小明", address, subject, attachments, unread);

    public static MailMessage Message(string body, params MailAttachmentInfo[] attachments) =>
        new(Mail("m1", "ming@example.com", "第三季報價", attachments.Length), ["a@corp.com"], [], body, attachments);

    public static string Json(object args) => System.Text.Json.JsonSerializer.Serialize(args);
}
