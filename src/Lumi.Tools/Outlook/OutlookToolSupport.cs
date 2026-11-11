using System.Globalization;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;

namespace Lumi.Tools.Outlook;

/// <summary>
/// Outlook 工具共用的規則：日期、可用性、資料區塊的包法、網域警告、附件與寄出前的檢查（M30 設計 1、3、5、6）。
/// </summary>
internal static class OutlookToolSupport
{
    public const int MaxRecipients = 20;
    public const int MaxBodyChars = 20_000;
    public const int MaxSubjectChars = 200;
    public const int MaxAttachments = 10;
    public const long MaxAttachmentBytes = 20L * 1024 * 1024;
    public const int MaxCalendarDays = 62;
    public const int MaxCalendarResults = 200;

    public const string DateFormatMessage = "日期格式要是 yyyy-MM-dd 或 yyyy-MM-ddTHH:mm。";

    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-ddTHH:mm";
    private const string Weekdays = "日一二三四五六";

    // ---- 日期 ----

    /// <summary>只接受 yyyy-MM-dd。成功回 null，失敗回給模型看的錯誤訊息。</summary>
    public static string? ParseDate(string? text, out DateTime value) =>
        DateTime.TryParseExact(text?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            ? null
            : DateFormatMessage;

    /// <summary>接受 yyyy-MM-ddTHH:mm，也接受 yyyy-MM-dd（當成當天 00:00，<paramref name="dateOnly"/> 為 true）。</summary>
    public static string? ParseDateTime(string? text, out DateTime value, out bool dateOnly)
    {
        var trimmed = text?.Trim();
        dateOnly = false;
        if (DateTime.TryParseExact(trimmed, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            return null;
        if (DateTime.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            dateOnly = true;
            return null;
        }
        value = default;
        return DateFormatMessage;
    }

    public static string FormatDateTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string FormatDate(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>行程的時間段：全天只寫日期，否則寫「日期 開始–結束」。</summary>
    public static string FormatRange(DateTime start, DateTime end, bool allDay) =>
        allDay
            ? $"{FormatDate(start)} 全天"
            : $"{FormatDate(start)} {start:HH:mm}–{end:HH:mm}";

    /// <summary>行程清單用的「11/10（一）」。</summary>
    public static string ShortDayWithWeekday(DateTime value) =>
        $"{value.ToString("MM/dd", CultureInfo.InvariantCulture)}（{Weekdays[(int)value.DayOfWeek]}）";

    // ---- 資料夾與可用性 ----

    public static bool TryParseFolder(string? text, out OutlookFolder folder, out string? error)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "inbox":
                folder = OutlookFolder.Inbox;
                error = null;
                return true;
            case "sent":
                folder = OutlookFolder.Sent;
                error = null;
                return true;
            case "drafts":
                folder = OutlookFolder.Drafts;
                error = null;
                return true;
            case "archive":
                folder = OutlookFolder.Archive;
                error = null;
                return true;
            default:
                folder = OutlookFolder.Inbox;
                error = "folder 只能是 inbox、sent、drafts、archive。";
                return false;
        }
    }

    /// <summary>給使用者與模型看的資料夾名稱。</summary>
    public static string FolderName(OutlookFolder folder) => folder switch
    {
        OutlookFolder.Inbox => "收件匣",
        OutlookFolder.Sent => "寄件備份",
        OutlookFolder.Drafts => "草稿",
        OutlookFolder.Archive => "封存",
        _ => folder.ToString(),
    };

    /// <summary>三個狀態 → 工具可用性（設計 3）。Outlook 沒開（NotRunning）算可用：呼叫時才回「請先開啟 Outlook」。</summary>
    public static ToolAvailability Availability(IOutlookClient client)
    {
        var status = client.GetStatus();
        return status.State switch
        {
            OutlookState.NotSupported => new ToolAvailability(ToolAvailabilityKind.NotOnThisPlatform, status.Message),
            OutlookState.NotInstalled => new ToolAvailability(ToolAvailabilityKind.Unavailable, OutlookToolText.NotInstalled),
            _ => ToolAvailability.Yes,
        };
    }

    // ---- 資料區塊 ----

    /// <summary>單一屬性：<c>名稱="值"</c>，值裡的 &amp; " &lt; &gt; 會跳脫。</summary>
    public static string Attr(string name, string value) =>
        $"{name}=\"{value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)}\"";

    /// <summary>多個屬性用空白接起來。</summary>
    public static string Attrs(params (string Name, string Value)[] pairs) =>
        string.Join(" ", pairs.Select(p => Attr(p.Name, p.Value)));

    /// <summary>
    /// 包成 <c>&lt;tag attributes&gt;\n內容\n&lt;/tag&gt;</c>。內容裡的 <c>&lt;/outlook_</c>（不分大小寫）改成 <c>&amp;lt;/outlook_</c>，
    /// 免得信件內容提早結束區塊。<paramref name="attributes"/> 要先用 <see cref="Attr"/> 組好。
    /// </summary>
    public static string Wrap(string tag, string attributes, string content)
    {
        var escaped = content.Replace("</outlook_", "&lt;/outlook_", StringComparison.OrdinalIgnoreCase);
        var open = attributes.Length == 0 ? tag : $"{tag} {attributes}";
        return $"<{open}>\n{escaped}\n</{tag}>";
    }

    /// <summary>寄件者顯示：「名稱 &lt;地址&gt;」；沒有名稱只寫地址。</summary>
    public static string Sender(string name, string address)
    {
        if (string.IsNullOrWhiteSpace(name))
            return address;
        return string.IsNullOrWhiteSpace(address) ? name : $"{name} <{address}>";
    }

    public static string Subject(string? subject) => string.IsNullOrWhiteSpace(subject) ? "（無主旨）" : subject.Trim();

    /// <summary>收件人清單的顯示；空的時候寫「（無）」。</summary>
    public static string Names(IEnumerable<string>? items)
    {
        var list = items?.ToList() ?? new List<string>();
        return list.Count == 0 ? "（無）" : string.Join("、", list);
    }

    /// <summary>去掉空白與空字串；全部是空的就回空清單。</summary>
    public static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? Array.Empty<string>()).Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).ToList();

    // ---- 網域警告（D9） ----

    /// <summary>
    /// 收件人裡有完整地址、網域和自己帳號不同的，回一則警告；自己的地址還沒知道、或都在同一個網域時回 null。
    /// 只寫名稱（沒有 @）的收件人不判斷。
    /// </summary>
    public static string? DomainWarning(IOutlookClient client, IEnumerable<string> recipients)
    {
        var ownDomain = OutlookAddresses.DomainOf(client.CachedOwnAddress);
        if (ownDomain is null)
            return null;

        var others = recipients
            .Select(r => (r ?? "").Trim())
            .Where(r => OutlookAddresses.DomainOf(r) is { } domain && domain != ownDomain)
            .ToList();
        if (others.Count == 0)
            return null;

        return $"收件人有 {others.Count} 個不在 {ownDomain}：{string.Join("、", others.Take(3))}";
    }

    // ---- 附件（draft、send_to_self、send 共用） ----

    /// <summary>
    /// 每個路徑經範圍解析，必須是存在的檔案、每個 20 MB 以內、最多 10 個。成功回完整路徑，失敗回給模型看的訊息。
    /// </summary>
    public static (IReadOnlyList<string>? Paths, string? Error) ResolveAttachments(IFileScope scope, IReadOnlyList<string>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
            return ([], null);
        if (attachments.Count > MaxAttachments)
            return (null, $"一次最多附加 {MaxAttachments} 個檔案。");

        var paths = new List<string>(attachments.Count);
        foreach (var raw in attachments)
        {
            var path = (raw ?? "").Trim();
            if (path.Length == 0)
                return (null, "附件路徑不能是空的。");

            var resolved = scope.Resolve(path);
            if (!resolved.IsAllowed)
                return (null, resolved.Error);

            var full = resolved.FullPath!;
            var display = scope.ToDisplayPath(full);
            if (Directory.Exists(full))
                return (null, $"「{display}」是資料夾，附件要選檔案。");
            if (!File.Exists(full))
                return (null, $"找不到附件「{display}」。");
            if (new FileInfo(full).Length > MaxAttachmentBytes)
                return (null, $"附件「{display}」超過 20 MB，不能附加。");

            paths.Add(full);
        }
        return (paths, null);
    }

    /// <summary>只用來顯示確認卡：附件解析失敗時回錯誤，成功回 null。</summary>
    public static string? AttachmentError(IFileScope scope, IReadOnlyList<string>? attachments) =>
        ResolveAttachments(scope, attachments).Error;

    /// <summary>附件在確認卡上的顯示名稱（範圍內的相對路徑）。</summary>
    public static string AttachmentNames(IFileScope scope, IEnumerable<string> fullPaths) =>
        string.Join("、", fullPaths.Select(scope.ToDisplayPath));

    // ---- 文字與寄出的檢查 ----

    /// <summary>kind 的正規化：去空白、小寫。</summary>
    public static string NormalizeKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant();

    /// <summary>草稿可用的 kind；不認得回 null。</summary>
    public static DraftKind? ParseDraftKind(string? kind) => NormalizeKind(kind) switch
    {
        "new" => DraftKind.New,
        "reply" => DraftKind.Reply,
        "reply_all" => DraftKind.ReplyAll,
        "forward" => DraftKind.Forward,
        _ => null,
    };

    /// <summary>內文：不能是空的，也不能超過 20,000 字。</summary>
    public static string? ValidateBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "內容（body）不能是空的。";
        if (body.Length > MaxBodyChars)
            return "內容（body）最多 20,000 字。";
        return null;
    }

    /// <summary>草稿的參數檢查。回覆、全部回覆、轉寄一定要有原信代號。</summary>
    public static string? ValidateDraft(OutlookMailDraftArgs args)
    {
        var kind = ParseDraftKind(args.Kind);
        if (kind is null)
            return "kind 只能是 new、reply、reply_all、forward。";
        if (kind != DraftKind.New && string.IsNullOrWhiteSpace(args.SourceId))
            return "回覆或轉寄要指定 source_id。";
        return ValidateBody(args.Body);
    }

    /// <summary>寄出的參數檢查（D14）。成功回 null。</summary>
    public static string? ValidateSend(OutlookMailSendArgs args)
    {
        var kind = NormalizeKind(args.Kind);
        if (kind is not ("new" or "reply" or "forward"))
            return "kind 只能是 new、reply、forward；要回給多人請把地址都列在 to 或 cc。";

        var to = args.To ?? Array.Empty<string>();
        if (to.Length == 0)
            return "寄出至少要有一位收件人（to）。";

        var addresses = to.Concat(args.Cc ?? Array.Empty<string>()).Concat(args.Bcc ?? Array.Empty<string>()).Select(a => (a ?? "").Trim()).ToList();
        foreach (var address in addresses.Where(a => a.Length > 0))
        {
            if (!OutlookAddresses.IsEmailAddress(address))
                return $"寄出需要完整的電子郵件地址（{address}）；只知道名稱時請改用 outlook_mail_draft。";
        }
        if (addresses.Count(a => a.Length > 0) > MaxRecipients)
            return "一次最多寄給 20 個收件人。";

        if (kind == "new" && string.IsNullOrWhiteSpace(args.Subject))
            return "新信要有主旨。";
        if (kind != "new" && string.IsNullOrWhiteSpace(args.SourceId))
            return "回覆或轉寄要指定 source_id。";

        return ValidateBody(args.Body);
    }

    /// <summary>寄給自己的參數檢查（D13）：主旨 1 到 200 字、內文 1 到 20,000 字。</summary>
    public static string? ValidateSelf(OutlookMailSendToSelfArgs args)
    {
        var subject = (args.Subject ?? "").Trim();
        if (subject.Length == 0)
            return "主旨不能是空的。";
        if (subject.Length > MaxSubjectChars)
            return "主旨最多 200 字。";
        return ValidateBody(args.Body);
    }
}
