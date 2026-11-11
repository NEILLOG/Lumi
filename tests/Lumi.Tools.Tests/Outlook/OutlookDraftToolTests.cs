using Lumi.Core.Chat;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;
using Lumi.Tools.Outlook;
using Lumi.Tools.Tests.Files;
using Xunit;
using static Lumi.Tools.Tests.Outlook.OutlookTestSupport;

namespace Lumi.Tools.Tests.Outlook;

/// <summary>M30 設計 1、5、6：草稿、會議邀請、行程與附件存檔的結果文字、確認卡與範圍檢查。</summary>
public sealed class OutlookDraftToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly FakeOutlookClient _client = new();

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private OutlookMailDraftTool Draft() => new(_client);

    [Fact]
    public void Descriptor_is_write_workbench_and_warns_after_external_content()
    {
        var d = Draft().Descriptor;

        Assert.Equal("outlook_mail_draft", d.Name);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.True(d.WarnsAfterExternalContent);
        Assert.True(d.CanAlwaysAllow);
    }

    [Fact]
    public async Task Draft_creates_draft_and_says_it_was_not_sent()
    {
        var tool = Draft();

        var result = await tool.ExecuteAsync(Json(new { kind = "new", subject = "報價", body = "您好", to = new[] { "a@corp.com" } }), Context(), default);

        Assert.False(result.IsError);
        Assert.Contains("已建立草稿並在 Outlook 打開，還沒有寄出。", result.Content);
        Assert.Contains("收件者：a@corp.com", result.Content);
        Assert.Contains("請使用者檢查後在 Outlook 按「傳送」。", result.Content);
        Assert.Equal("已建立草稿", result.Summary);
        Assert.Empty(_client.Sends);
    }

    [Fact]
    public async Task Draft_requires_source_for_reply()
    {
        var tool = Draft();
        var json = Json(new { kind = "reply", body = "好的" });

        var result = await tool.ExecuteAsync(json, Context(), default);
        var preview = tool.Preview(json, Context());

        Assert.True(result.IsError);
        Assert.Contains("回覆或轉寄要指定 source_id。", result.Content);
        Assert.Equal("回覆或轉寄要指定 source_id。", preview.Rejection);
        Assert.Empty(_client.Drafts);
    }

    [Fact]
    public async Task Draft_rejects_unknown_kind_and_empty_body()
    {
        var tool = Draft();

        Assert.True((await tool.ExecuteAsync(Json(new { kind = "all", body = "x" }), Context(), default)).IsError);
        Assert.True((await tool.ExecuteAsync(Json(new { kind = "new", body = "  " }), Context(), default)).IsError);
        Assert.Empty(_client.Drafts);
    }

    [Fact]
    public void Draft_domain_warning()
    {
        _client.OwnAddress = "a@corp.com";
        var tool = Draft();

        var preview = tool.Preview(Json(new { kind = "new", body = "x", to = new[] { "b@corp.com", "c@gmail.com" } }), Context());

        var warning = Assert.Single(preview.Warnings!);
        Assert.Contains("c@gmail.com", warning);
        Assert.DoesNotContain("b@corp.com", warning);
    }

    [Fact]
    public void Draft_no_warning_without_own_address()
    {
        var tool = Draft();

        var preview = tool.Preview(Json(new { kind = "new", body = "x", to = new[] { "c@gmail.com" } }), Context());

        Assert.Null(preview.Warnings);
    }

    [Fact]
    public async Task Draft_attachment_must_be_in_scope()
    {
        var outsideFile = _outside.Write("secret.txt");
        var tool = Draft();
        var json = Json(new { kind = "new", body = "x", attachments = new[] { outsideFile } });

        var result = await tool.ExecuteAsync(json, Context(ScopeOf(_tree)), default);
        var preview = tool.Preview(json, Context(ScopeOf(_tree)));

        Assert.True(result.IsError);
        Assert.NotNull(preview.Rejection);
        Assert.Empty(_client.Drafts);
    }

    [Fact]
    public async Task Draft_passes_resolved_attachment_paths()
    {
        var file = _tree.Write("下載/報告.pdf");
        var tool = Draft();
        var json = Json(new { kind = "new", body = "請看附件", attachments = new[] { "下載/報告.pdf" } });

        var result = await tool.ExecuteAsync(json, Context(ScopeOf(_tree)), default);

        Assert.False(result.IsError);
        var request = Assert.Single(_client.Drafts);
        var attachment = Assert.Single(request.Attachments);
        Assert.EndsWith(Path.Combine("下載", "報告.pdf"), attachment);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Draft_not_running_returns_error_without_calling_client()
    {
        _client.Status = new OutlookStatus(OutlookState.NotRunning, OutlookToolText.NotRunning);

        var result = await Draft().ExecuteAsync(Json(new { kind = "new", body = "x" }), Context(), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public void Draft_preview_target_falls_back_to_source()
    {
        var preview = Draft().Preview(Json(new { kind = "reply", source_id = "m1", body = "好" }), Context());

        Assert.Equal("回覆：m1", preview.Target);
        Assert.Null(preview.Rejection);
    }

    // ---- 會議邀請 ----

    [Fact]
    public async Task Meeting_draft_opens_without_sending()
    {
        var tool = new OutlookMeetingDraftTool(_client);
        var json = Json(new { subject = "週會", start = "2026-11-10T14:00", end = "2026-11-10T15:00", required = new[] { "王小明" } });

        var result = await tool.ExecuteAsync(json, Context(), default);

        Assert.False(result.IsError);
        var meeting = Assert.Single(_client.MeetingDrafts);
        Assert.Equal(new DateTime(2026, 11, 10, 14, 0, 0), meeting.Start);
        Assert.Contains("已建立會議邀請草稿並在 Outlook 打開，還沒有寄出。", result.Content);
        Assert.Equal(new[] { "OpenMeetingDraft" }, _client.Calls);
    }

    [Fact]
    public async Task Meeting_requires_attendees_and_order()
    {
        var tool = new OutlookMeetingDraftTool(_client);

        var noAttendee = await tool.ExecuteAsync(Json(new { subject = "週會", start = "2026-11-10T14:00", end = "2026-11-10T15:00", required = Array.Empty<string>() }), Context(), default);
        var reversed = await tool.ExecuteAsync(Json(new { subject = "週會", start = "2026-11-10T15:00", end = "2026-11-10T14:00", required = new[] { "a" } }), Context(), default);

        Assert.True(noAttendee.IsError);
        Assert.True(reversed.IsError);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public void Meeting_domain_warning_checks_attendees()
    {
        _client.OwnAddress = "a@corp.com";
        var tool = new OutlookMeetingDraftTool(_client);

        var preview = tool.Preview(Json(new { subject = "週會", start = "2026-11-10T14:00", end = "2026-11-10T15:00", required = new[] { "b@corp.com" }, optional = new[] { "x@other.org" } }), Context());

        Assert.Single(preview.Warnings!);
        Assert.Contains("x@other.org", preview.Warnings![0]);
    }

    // ---- 行程 ----

    [Fact]
    public async Task Calendar_save_creates_event()
    {
        var tool = new OutlookCalendarSaveTool(_client);

        var result = await tool.ExecuteAsync(Json(new { subject = "看牙醫", start = "2026-11-10T09:00", end = "2026-11-10T10:00", location = "中山路" }), Context(), default);

        Assert.False(result.IsError);
        var save = Assert.Single(_client.EventSaves);
        Assert.Null(save.Id);
        Assert.Equal("中山路", save.Location);
        Assert.Equal("已新增行程", result.Summary);
    }

    [Fact]
    public async Task Calendar_save_all_day_end_is_inclusive()
    {
        var tool = new OutlookCalendarSaveTool(_client);

        await tool.ExecuteAsync(Json(new { subject = "出差", start = "2026-11-10", end = "2026-11-11", all_day = true }), Context(), default);

        var save = Assert.Single(_client.EventSaves);
        Assert.Equal(new DateTime(2026, 11, 12), save.End);
        Assert.True(save.AllDay);
    }

    [Fact]
    public async Task Calendar_save_rejects_reminder_out_of_range()
    {
        var tool = new OutlookCalendarSaveTool(_client);

        var result = await tool.ExecuteAsync(Json(new { subject = "x", start = "2026-11-10T09:00", end = "2026-11-10T10:00", reminder_minutes = 10081 }), Context(), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public void Calendar_save_preview_mentions_modification()
    {
        var preview = new OutlookCalendarSaveTool(_client).Preview(Json(new { id = "e3", subject = "改時間", start = "2026-11-10T09:00", end = "2026-11-10T10:00" }), Context());

        Assert.Equal("改時間", preview.Target);
        Assert.Contains("修改既有行程 e3", preview.Detail);
    }

    // ---- 附件存檔 ----

    [Fact]
    public async Task Save_attachment_renames_on_conflict()
    {
        _tree.Dir("下載");
        _tree.Write("下載/報告.pdf");
        _client.Mail = Message("內文", new MailAttachmentInfo(1, "報告.pdf", 100));
        var tool = new OutlookMailSaveAttachmentTool(_client);

        var result = await tool.ExecuteAsync(Json(new { id = "m1", attachment = 1, folder = "下載" }), Context(ScopeOf(_tree)), default);

        Assert.False(result.IsError);
        var target = Assert.Single(_client.SaveTargets);
        Assert.EndsWith(Path.Combine("下載", "報告 (1).pdf"), target);
        Assert.Contains("已存到", result.Content);
    }

    [Fact]
    public async Task Save_attachment_uses_original_name_when_free()
    {
        _tree.Dir("下載");
        _client.Mail = Message("內文", new MailAttachmentInfo(2, "a/b\\說明.pdf", 100));
        var tool = new OutlookMailSaveAttachmentTool(_client);

        var result = await tool.ExecuteAsync(Json(new { id = "m1", attachment = 2, folder = "下載" }), Context(ScopeOf(_tree)), default);

        Assert.False(result.IsError);
        Assert.EndsWith(Path.Combine("下載", "ab說明.pdf"), Assert.Single(_client.SaveTargets));
    }

    [Fact]
    public async Task Save_attachment_defaults_to_the_only_folder()
    {
        _client.Mail = Message("內文", new MailAttachmentInfo(1, "a.txt", 1));
        var tool = new OutlookMailSaveAttachmentTool(_client);

        var result = await tool.ExecuteAsync(Json(new { id = "m1", attachment = 1 }), Context(ScopeOf(_tree)), default);

        Assert.False(result.IsError);
        Assert.EndsWith("a.txt", Assert.Single(_client.SaveTargets));
    }

    [Fact]
    public async Task Save_attachment_without_folder_and_several_roots_is_error()
    {
        var scope = new FileScope([new ScopeRoot("甲", _tree.Root, true), new ScopeRoot("乙", _outside.Root, true)]);
        _client.Mail = Message("內文", new MailAttachmentInfo(1, "a.txt", 1));
        var tool = new OutlookMailSaveAttachmentTool(_client);

        var result = await tool.ExecuteAsync(Json(new { id = "m1", attachment = 1 }), Context(scope), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.SaveTargets);
    }

    [Fact]
    public async Task Save_attachment_missing_index_is_error()
    {
        _client.Mail = Message("內文", new MailAttachmentInfo(1, "a.txt", 1));
        var tool = new OutlookMailSaveAttachmentTool(_client);

        var result = await tool.ExecuteAsync(Json(new { id = "m1", attachment = 3 }), Context(ScopeOf(_tree)), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.SaveTargets);
    }

    [Fact]
    public async Task Save_attachment_folder_outside_scope_is_error()
    {
        _client.Mail = Message("內文", new MailAttachmentInfo(1, "a.txt", 1));
        var tool = new OutlookMailSaveAttachmentTool(_client);

        var result = await tool.ExecuteAsync(Json(new { id = "m1", attachment = 1, folder = _outside.Root }), Context(ScopeOf(_tree)), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.SaveTargets);
    }
}
