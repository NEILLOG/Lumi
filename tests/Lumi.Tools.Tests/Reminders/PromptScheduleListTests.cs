using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Tools.Reminders;
using Lumi.Tools.Tests.Memory;

namespace Lumi.Tools.Tests.Reminders;

/// <summary>M24 24D：<c>list_reminders</c> 的 <c>action</c>／<c>grant</c>／<c>prompt</c>，以及取消、啟停對「跑 prompt」排程的行為。</summary>
public sealed class PromptScheduleListTests
{
    // 2026-10-23（週五）09:05 台北
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 5, 0, TimeSpan.Zero);

    private readonly ReminderRig _rig = new(Now);
    private readonly FakePromptReminderService _service;
    private readonly CreateReminderTool _create;
    private readonly CreateScheduledPromptTool _createPrompt;
    private readonly ListRemindersTool _list;
    private readonly CancelReminderTool _cancel;
    private readonly SetReminderEnabledTool _enable;

    public PromptScheduleListTests()
    {
        _service = new FakePromptReminderService(_rig);
        _create = new CreateReminderTool(_service, _rig.Options);
        _createPrompt = new CreateScheduledPromptTool(_service, _rig.Options, new FakeModelCatalog());
        _list = new ListRemindersTool(_service);
        _cancel = new CancelReminderTool(_service);
        _enable = new SetReminderEnabledTool(_service);
    }

    private ToolContext Context => new("c1", ChatMode.Workbench, _rig.Clock, new FakeFileScope());

    private Task<ToolResult> Run(ITool tool, string json) => tool.ExecuteAsync(json, Context, default);

    private async Task<string> CreatePrompt(string json)
    {
        var result = await Run(_createPrompt, json);
        Assert.False(result.IsError, result.Content);
        return ReminderJson.IdOf(result);
    }

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    // ---- list_reminders：action／grant／prompt ----

    [Fact]
    public async Task List_NotifyItem_HasActionNotifyAndNoGrantOrPrompt()
    {
        await Run(_create, """{"title":"開會","message":"準備投影片","at":"2026-10-25T09:00:00+08:00","time_zone":"Asia/Taipei"}""");

        var rows = ReminderJson.Parse((await Run(_list, "{}")).Content);

        var row = rows.EnumerateArray().Single();
        Assert.Equal("notify", row.GetProperty("action").GetString());
        Assert.Equal("開會", row.GetProperty("title").GetString());
        Assert.Equal("準備投影片", row.GetProperty("message").GetString());
        Assert.False(row.TryGetProperty("grant", out _));
        Assert.False(row.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task List_PromptItem_HasActionGrantAndPromptPreview()
    {
        var prompt = Repeat("字", 150);
        await CreatePrompt($$"""{"name":"晨間整理","prompt":"{{prompt}}","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei","suggested_permissions":["write"],"granted_permissions":["write","network"]}""");

        var row = ReminderJson.Parse((await Run(_list, "{}")).Content).EnumerateArray().Single();

        Assert.Equal("run_prompt", row.GetProperty("action").GetString());
        Assert.Equal("晨間整理", row.GetProperty("title").GetString());
        Assert.Equal(["read", "write", "network"], row.GetProperty("grant").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(Repeat("字", 100), row.GetProperty("prompt").GetString());
        // 既有欄位不退步
        Assert.Equal("schedule", row.GetProperty("kind").GetString());
        Assert.Equal("pending", row.GetProperty("status").GetString());
        Assert.True(row.GetProperty("enabled").GetBoolean());
        Assert.Equal("每天 09:00", row.GetProperty("recurrence").GetString());
    }

    [Fact]
    public async Task List_PromptItemShortPrompt_IsReturnedWhole()
    {
        await CreatePrompt("""{"name":"開會","prompt":"整理資料夾","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        var row = ReminderJson.Parse((await Run(_list, "{}")).Content).EnumerateArray().Single();

        Assert.Equal("整理資料夾", row.GetProperty("prompt").GetString());
        Assert.Equal(["read"], row.GetProperty("grant").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public async Task List_MixedItems_EachRowHasAction()
    {
        await Run(_create, """{"title":"開會","message":"m","at":"2026-10-25T09:00:00+08:00","time_zone":"Asia/Taipei"}""");
        await CreatePrompt("""{"name":"晨間整理","prompt":"p","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""");

        var actions = ReminderJson.Parse((await Run(_list, """{"status":"all"}""")).Content)
            .EnumerateArray()
            .Select(r => r.GetProperty("action").GetString())
            .ToList();

        Assert.Equal(2, actions.Count);
        Assert.Contains("notify", actions);
        Assert.Contains("run_prompt", actions);
    }

    [Fact]
    public void Description_MentionsRunPromptAction()
    {
        Assert.Contains("run_prompt", _list.Descriptor.Description);
    }

    // ---- cancel_reminder、set_reminder_enabled 對「跑 prompt」排程 ----

    [Fact]
    public async Task Cancel_PromptSchedule_CancelsItAndStatusIsCancelled()
    {
        var id = await CreatePrompt("""{"name":"晨間整理","prompt":"p","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""");

        var result = await Run(_cancel, $$"""{"id":"{{id}}"}""");

        Assert.False(result.IsError, result.Content);
        var status = (await Run(_list, """{"status":"cancelled"}""")).Content;
        Assert.Contains(id, status);
        Assert.Contains("run_prompt", status);
        Assert.Equal("cancelled", ReminderJson.Parse(status).EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Disable_PromptSchedule_ThenEnable_TogglesEnabledFlag()
    {
        var id = await CreatePrompt("""{"name":"晨間整理","prompt":"p","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""");

        var disabled = await Run(_enable, $$"""{"id":"{{id}}","enabled":false}""");
        Assert.False(disabled.IsError, disabled.Content);
        Assert.False(ReminderJson.Parse((await Run(_list, "{}")).Content).EnumerateArray().Single().GetProperty("enabled").GetBoolean());

        var enabled = await Run(_enable, $$"""{"id":"{{id}}","enabled":true}""");
        Assert.False(enabled.IsError, enabled.Content);
        Assert.True(ReminderJson.Parse((await Run(_list, "{}")).Content).EnumerateArray().Single().GetProperty("enabled").GetBoolean());
    }
}
