using System.Reflection;
using Lumi.Core.Chat;
using Lumi.Core.Outlook;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Tools.Outlook;
using Lumi.Tools.Tests.Files;
using Xunit;
using static Lumi.Tools.Tests.Outlook.OutlookTestSupport;

namespace Lumi.Tools.Tests.Outlook;

/// <summary>M30 設計 1、5、6；D13（寄給自己）與 D14（確認後寄出）的規則。</summary>
public sealed class OutlookSendToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeOutlookClient _client = new();

    public void Dispose() => _tree.Dispose();

    private static ToolApprovalRequest Request(ITool tool, ToolPermission permission, ToolPreview preview) =>
        new("call1", tool.Descriptor.Name, tool.Descriptor.DisplayName, permission, preview, "{}");

    // ---- 寄給自己（D13） ----

    [Fact]
    public void SendToSelf_args_have_no_recipient_fields()
    {
        var names = typeof(OutlookMailSendToSelfArgs).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

        Assert.DoesNotContain(names, n => n.Equals("To", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Cc", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Bcc", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Recipient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SendToSelf_is_allowed_by_schedule_write_grant()
    {
        var tool = new OutlookMailSendToSelfTool(_client);

        var verdict = ScheduledRunApprovalPolicy.Decide(tool.Descriptor, Request(tool, ToolPermission.Write, ToolPreview.None), new ScheduleGrant(true, false, false));

        Assert.Equal(ScheduledRunVerdict.Allow, verdict);
    }

    [Fact]
    public void SendToSelf_has_no_external_content_warning()
    {
        Assert.False(new OutlookMailSendToSelfTool(_client).Descriptor.WarnsAfterExternalContent);
    }

    [Fact]
    public void SendToSelf_is_write_workbench_and_can_always_allow()
    {
        var d = new OutlookMailSendToSelfTool(_client).Descriptor;

        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.True(d.CanAlwaysAllow);
        Assert.False(d.AlwaysAsk);
    }

    [Fact]
    public async Task SendToSelf_passes_subject_without_prefix_to_client()
    {
        var tool = new OutlookMailSendToSelfTool(_client);

        var result = await tool.ExecuteAsync(Json(new { subject = "週報", body = "本週摘要" }), Context(), default);

        Assert.False(result.IsError);
        var sent = Assert.Single(_client.SelfMails);
        Assert.Equal("週報", sent.Subject);
        Assert.Equal("本週摘要", sent.Body);
        Assert.Contains("[Lumi] 週報", result.Content);
        Assert.Contains("me@corp.com", result.Content);
        Assert.Equal("已寄給自己", result.Summary);
    }

    [Fact]
    public void SendToSelf_preview_has_lumi_prefix_and_no_recipients()
    {
        var preview = new OutlookMailSendToSelfTool(_client).Preview(Json(new { subject = "週報", body = "x" }), Context());

        Assert.Equal("[Lumi] 週報", preview.Target);
        Assert.Equal("寄到你自己的信箱", preview.Detail);
    }

    [Fact]
    public async Task SendToSelf_rejects_empty_subject_and_long_subject()
    {
        var tool = new OutlookMailSendToSelfTool(_client);

        Assert.True((await tool.ExecuteAsync(Json(new { subject = " ", body = "x" }), Context(), default)).IsError);
        Assert.True((await tool.ExecuteAsync(Json(new { subject = new string('字', 201), body = "x" }), Context(), default)).IsError);
        Assert.Empty(_client.SelfMails);
    }

    [Fact]
    public async Task SendToSelf_with_attachment_outside_scope_is_error()
    {
        var outside = _tree.Write("inside.txt");
        var tool = new OutlookMailSendToSelfTool(_client);
        var scope = new FileScope([new ScopeRoot("只有子資料夾", _tree.Dir("sub"), true)]);

        var result = await tool.ExecuteAsync(Json(new { subject = "x", body = "y", attachments = new[] { outside } }), Context(scope), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.SelfMails);
    }

    // ---- 寄出（D14） ----

    [Fact]
    public void Send_descriptor_is_destructive_always_ask()
    {
        var d = new OutlookMailSendTool(_client).Descriptor;

        Assert.Equal("outlook_mail_send", d.Name);
        Assert.Equal(ToolPermission.Destructive, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.True(d.AlwaysAsk);
        Assert.False(d.CanAlwaysAllow);
        Assert.True(d.WarnsAfterExternalContent);
    }

    [Fact]
    public void Send_always_asks_in_schedule()
    {
        var tool = new OutlookMailSendTool(_client);

        var verdict = ScheduledRunApprovalPolicy.Decide(tool.Descriptor, Request(tool, ToolPermission.Destructive, ToolPreview.None), new ScheduleGrant(true, true, true));

        Assert.Equal(ScheduledRunVerdict.Ask, verdict);
    }

    [Fact]
    public void Send_rejects_name_without_address()
    {
        var tool = new OutlookMailSendTool(_client);
        var json = Json(new { kind = "new", to = new[] { "王小明" }, subject = "x", body = "y" });

        var preview = tool.Preview(json, Context());

        Assert.NotNull(preview.Rejection);
        Assert.Contains("只知道名稱時請改用 outlook_mail_draft。", preview.Rejection);
    }

    [Fact]
    public async Task Send_rejects_name_without_address_when_executed()
    {
        var tool = new OutlookMailSendTool(_client);
        var json = Json(new { kind = "new", to = new[] { "王小明" }, subject = "x", body = "y" });

        var result = await tool.ExecuteAsync(json, Context(), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.Sends);
    }

    [Fact]
    public async Task Send_rejects_more_than_20_recipients()
    {
        var tool = new OutlookMailSendTool(_client);
        var to = Enumerable.Range(1, 21).Select(i => $"p{i}@corp.com").ToArray();
        var json = Json(new { kind = "new", to, subject = "x", body = "y" });

        var result = await tool.ExecuteAsync(json, Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("一次最多寄給 20 個收件人。", result.Content);
        Assert.Equal("一次最多寄給 20 個收件人。", tool.Preview(json, Context()).Rejection);
        Assert.Empty(_client.Sends);
    }

    [Fact]
    public async Task Send_new_requires_subject()
    {
        var tool = new OutlookMailSendTool(_client);
        var json = Json(new { kind = "new", to = new[] { "a@corp.com" }, body = "y" });

        var result = await tool.ExecuteAsync(json, Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("新信要有主旨。", result.Content);
        Assert.Empty(_client.Sends);
    }

    [Fact]
    public async Task Send_reply_requires_source()
    {
        var tool = new OutlookMailSendTool(_client);
        var json = Json(new { kind = "reply", to = new[] { "a@corp.com" }, body = "y" });

        var result = await tool.ExecuteAsync(json, Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("回覆或轉寄要指定 source_id。", result.Content);
        Assert.Empty(_client.Sends);
    }

    [Fact]
    public async Task Send_rejects_reply_all_kind()
    {
        var tool = new OutlookMailSendTool(_client);
        var json = Json(new { kind = "reply_all", source_id = "m1", to = new[] { "a@corp.com" }, body = "y" });

        var result = await tool.ExecuteAsync(json, Context(), default);

        Assert.True(result.IsError);
        Assert.Contains("kind 只能是 new、reply、forward", result.Content);
        Assert.Empty(_client.Sends);
    }

    [Fact]
    public void Send_preview_shows_full_body_and_recipients()
    {
        var body = new string('文', 5_000);
        var tool = new OutlookMailSendTool(_client);

        var preview = tool.Preview(Json(new { kind = "new", to = new[] { "a@corp.com" }, cc = new[] { "b@corp.com" }, subject = "報價", body }), Context());

        Assert.Null(preview.Rejection);
        Assert.Equal("報價", preview.Target);
        Assert.Contains("收件者：a@corp.com\n", preview.Detail);
        Assert.Contains("副本：b@corp.com\n", preview.Detail);
        Assert.DoesNotContain("密件副本", preview.Detail);
        Assert.Contains("---\n" + body, preview.Detail);
        Assert.EndsWith(body, preview.Detail);
    }

    [Fact]
    public void Send_preview_for_reply_says_original_is_attached()
    {
        var tool = new OutlookMailSendTool(_client);

        var preview = tool.Preview(Json(new { kind = "reply", source_id = "m1", to = new[] { "a@corp.com" }, body = "好" }), Context());

        Assert.Equal("回覆：m1", preview.Target);
        Assert.Contains("原信會附在下面\n---\n好", preview.Detail);
    }

    [Fact]
    public void Send_domain_warning()
    {
        _client.OwnAddress = "a@corp.com";
        var tool = new OutlookMailSendTool(_client);

        var preview = tool.Preview(Json(new { kind = "new", to = new[] { "b@corp.com", "c@gmail.com" }, subject = "x", body = "y" }), Context());

        var warning = Assert.Single(preview.Warnings!);
        Assert.Contains("c@gmail.com", warning);
        Assert.DoesNotContain("b@corp.com", warning);
    }

    [Fact]
    public async Task Send_result_lists_recipients()
    {
        var tool = new OutlookMailSendTool(_client);
        var json = Json(new { kind = "new", to = new[] { "a@corp.com" }, cc = new[] { "b@corp.com" }, subject = "報價", body = "y" });

        var result = await tool.ExecuteAsync(json, Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已寄出。收件者：a@corp.com；副本：b@corp.com；主旨：報價", result.Content);
        Assert.Equal("已寄出", result.Summary);
        var sent = Assert.Single(_client.Sends);
        Assert.Equal(DraftKind.New, sent.Kind);
    }

    [Fact]
    public async Task Send_not_running_returns_error_without_calling_client()
    {
        _client.Status = new OutlookStatus(OutlookState.NotRunning, OutlookToolText.NotRunning);
        var tool = new OutlookMailSendTool(_client);

        var result = await tool.ExecuteAsync(Json(new { kind = "new", to = new[] { "a@corp.com" }, subject = "x", body = "y" }), Context(), default);

        Assert.True(result.IsError);
        Assert.Empty(_client.Calls);
    }
}
