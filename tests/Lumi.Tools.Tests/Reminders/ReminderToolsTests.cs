using System.Globalization;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Tools.Reminders;

namespace Lumi.Tools.Tests.Reminders;

/// <summary>M10 10E2：<c>start_timer</c>、<c>list_reminders</c>、<c>cancel_reminder</c>、<c>set_reminder_enabled</c>。</summary>
public sealed class ReminderToolsTests
{
    // 2026-10-23（週五）09:05 台北
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 5, 0, TimeSpan.Zero);

    private readonly ReminderRig _rig = new(Now);
    private readonly CreateReminderTool _create;
    private readonly StartTimerTool _timer;
    private readonly ListRemindersTool _list;
    private readonly CancelReminderTool _cancel;
    private readonly SetReminderEnabledTool _enable;

    public ReminderToolsTests()
    {
        _create = new CreateReminderTool(_rig.Service, _rig.Options);
        _timer = new StartTimerTool(_rig.Service, _rig.Options);
        _list = new ListRemindersTool(_rig.Service);
        _cancel = new CancelReminderTool(_rig.Service);
        _enable = new SetReminderEnabledTool(_rig.Service);
    }

    private Task<ToolResult> Run(ITool tool, string json) => tool.ExecuteAsync(json, _rig.Context, default);

    private static string LocalO(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).ToString("O", CultureInfo.InvariantCulture);

    private static string LocalClock(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);

    private async Task<string> CreateOneTime(string title = "開會", string at = "2026-10-25T09:00:00+08:00")
    {
        var result = await Run(_create, $$"""{"title":"{{title}}","message":"m","at":"{{at}}","time_zone":"Asia/Taipei"}""");
        Assert.False(result.IsError, result.Content);
        return ReminderJson.IdOf(result);
    }

    private async Task<string> StartTimer(int seconds, string? label = null)
    {
        var json = label is null
            ? $$"""{"duration_seconds":{{seconds}}}"""
            : $$"""{"duration_seconds":{{seconds}},"label":"{{label}}"}""";
        var result = await Run(_timer, json);
        Assert.False(result.IsError, result.Content);
        return ReminderJson.IdOf(result);
    }

    /// <summary>把時間推到 <paramref name="when"/>，並讓背景流程處理到點的項目（和 App 的迴圈相同）。</summary>
    private async Task AdvanceAndProcess(DateTimeOffset when)
    {
        _rig.Clock.SetNow(when);
        await _rig.Service.ProcessDueAsync(default);
    }

    // ---- start_timer ----

    [Fact]
    public async Task StartTimer_ReturnsIdLabelDurationAndExpiry()
    {
        var result = await Run(_timer, """{"duration_seconds":600,"label":"煮麵"}""");

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal("煮麵", json.GetProperty("label").GetString());
        Assert.Equal(600, json.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(LocalO(Now.AddSeconds(600)), json.GetProperty("expires_at").GetString());
        Assert.Contains($"約 {LocalClock(Now.AddSeconds(600))}", result.Summary);
        Assert.Equal(ScheduledKind.Timer, Assert.Single(await _rig.Store.ListAsync(new ScheduledItemFilter(), default)).Kind);
    }

    [Theory]
    [InlineData("""{"duration_seconds":600}""")]
    [InlineData("""{"duration_seconds":600,"label":"   "}""")]
    public async Task StartTimer_LabelDefaultsToTimer(string json)
    {
        var result = await Run(_timer, json);

        Assert.Equal("計時器", ReminderJson.Parse(result.Content).GetProperty("label").GetString());
    }

    [Fact]
    public async Task StartTimer_LabelIsTrimmed()
    {
        var result = await Run(_timer, """{"duration_seconds":600,"label":"  煮麵  "}""");

        Assert.Equal("煮麵", ReminderJson.Parse(result.Content).GetProperty("label").GetString());
    }

    [Fact]
    public async Task StartTimer_LabelLengthLimit()
    {
        var atLimit = await Run(_timer, $$"""{"duration_seconds":600,"label":"{{new string('名', 100)}}"}""");
        var tooLong = await Run(_timer, $$"""{"duration_seconds":600,"label":"{{new string('名', 101)}}"}""");

        Assert.False(atLimit.IsError);
        Assert.True(tooLong.IsError);
        Assert.Contains("label 太長（上限 100 字）", tooLong.Content);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(86401)]
    [InlineData(0)]
    public async Task StartTimer_DurationOutOfRange_IsError(int seconds)
    {
        var result = await Run(_timer, $$"""{"duration_seconds":{{seconds}}}""");

        Assert.True(result.IsError);
        Assert.Contains("duration_seconds 必須介於 10 到 86400 秒 (24 小時) 之間", result.Content);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(86400)]
    public async Task StartTimer_DurationBoundsAreAccepted(int seconds)
    {
        var result = await Run(_timer, $$"""{"duration_seconds":{{seconds}}}""");

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task StartTimer_MissingDuration_IsArgumentError()
    {
        var result = await Run(_timer, """{"label":"煮麵"}""");

        Assert.True(result.IsError);
    }

    [Fact]
    public void StartTimer_Preview_ShowsLabelDurationAndExpiryClock()
    {
        var preview = _timer.Preview("""{"duration_seconds":600,"label":"煮麵"}""", _rig.Context);

        Assert.Equal("煮麵", preview.Target);
        Assert.Equal($"10 分鐘後（約 {LocalClock(Now.AddSeconds(600))}）", preview.Detail);
    }

    [Fact]
    public void StartTimer_Preview_DefaultLabelAndNoDetailWhenOutOfRange()
    {
        Assert.Equal("計時器", _timer.Preview("""{"duration_seconds":600}""", _rig.Context).Target);
        Assert.Null(_timer.Preview("""{"duration_seconds":5}""", _rig.Context).Detail);
    }

    [Fact]
    public void StartTimer_Descriptor()
    {
        var d = _timer.Descriptor;

        Assert.Equal("start_timer", d.Name);
        Assert.Equal("啟動計時器", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
        Assert.Contains("沒有在執行", d.Description);
    }

    // ---- list_reminders ----

    [Fact]
    public async Task List_Empty_ReturnsEmptyArray()
    {
        var result = await Run(_list, "{}");

        Assert.False(result.IsError);
        Assert.Equal("[]", result.Content);
        Assert.Equal("0 個提醒", result.Summary);
    }

    /// <summary>種三筆：一次性提醒、計時器、每天的排程。</summary>
    private async Task SeedThree()
    {
        await CreateOneTime("開會");
        await StartTimer(600, "煮麵");
        var daily = await Run(_create, """{"title":"喝水","message":"m","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""");
        Assert.False(daily.IsError);
    }

    [Fact]
    public async Task List_DefaultIsPending_WithKindsAndRecurrenceOnlyForSchedules()
    {
        await SeedThree();

        var result = await Run(_list, "{}");

        Assert.Equal("3 個提醒", result.Summary);
        var rows = ReminderJson.Parse(result.Content).EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        var reminder = rows.Single(r => r.GetProperty("title").GetString() == "開會");
        Assert.Equal("reminder", reminder.GetProperty("kind").GetString());
        Assert.Equal("pending", reminder.GetProperty("status").GetString());
        Assert.True(reminder.GetProperty("enabled").GetBoolean());
        Assert.Equal("2026-10-25T09:00:00.0000000+08:00", reminder.GetProperty("next_fire_at").GetString());
        Assert.False(reminder.TryGetProperty("last_fired_at", out _));
        Assert.False(reminder.TryGetProperty("recurrence", out _));

        var timer = rows.Single(r => r.GetProperty("kind").GetString() == "timer");
        Assert.Equal("煮麵", timer.GetProperty("title").GetString());

        var schedule = rows.Single(r => r.GetProperty("kind").GetString() == "schedule");
        Assert.Equal("每天 09:00", schedule.GetProperty("recurrence").GetString());
    }

    [Theory]
    [InlineData("""{"kind":"timer"}""", 1)]
    [InlineData("""{"kind":" TIMER "}""", 1)]
    [InlineData("""{"kind":"schedule"}""", 1)]
    [InlineData("""{"kind":"all"}""", 3)]
    [InlineData("""{"status":"PENDING"}""", 3)]
    public async Task List_FiltersByKindAndStatus(string json, int expected)
    {
        await SeedThree();

        var result = await Run(_list, json);

        Assert.False(result.IsError);
        Assert.Equal(expected, ReminderJson.Parse(result.Content).GetArrayLength());
    }

    [Fact]
    public async Task List_StatusAll_IncludesCancelled_DefaultDoesNot()
    {
        await SeedThree();
        var id = await CreateOneTime("待取消");
        await Run(_cancel, $$"""{"id":"{{id}}"}""");

        var pending = await Run(_list, "{}");
        var all = await Run(_list, """{"status":"all"}""");

        Assert.Equal(3, ReminderJson.Parse(pending.Content).GetArrayLength());
        var rows = ReminderJson.Parse(all.Content).EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal("cancelled", rows.Single(r => r.GetProperty("title").GetString() == "待取消").GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_StatusCompleted_AfterTimerFires_ShowsLastFiredAt()
    {
        await StartTimer(600, "煮麵");
        await AdvanceAndProcess(Now.AddSeconds(601));

        var result = await Run(_list, """{"status":"completed"}""");

        var row = Assert.Single(ReminderJson.Parse(result.Content).EnumerateArray());
        Assert.Equal("completed", row.GetProperty("status").GetString());
        Assert.True(row.TryGetProperty("last_fired_at", out _));
    }

    [Fact]
    public async Task List_StatusMissed_AfterStartupGraceExpires()
    {
        await CreateOneTime("開會", "2026-10-25T09:00:00+08:00");
        // 台北 10/25 09:10 才啟動：超過 2 分鐘寬限，一次性提醒標為錯過（不響）
        _rig.Clock.SetNow(new DateTimeOffset(2026, 10, 25, 9, 10, 0, TimeSpan.FromHours(8)));
        await _rig.Service.OnStartupAsync(default);

        var result = await Run(_list, """{"status":"missed"}""");

        var row = Assert.Single(ReminderJson.Parse(result.Content).EnumerateArray());
        Assert.Equal("開會", row.GetProperty("title").GetString());
        Assert.Equal("missed", row.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""{"status":"done"}""", "status 只能是")]
    [InlineData("""{"kind":"alarm"}""", "kind 只能是")]
    public async Task List_UnknownFilter_IsClearError(string json, string expected)
    {
        var result = await Run(_list, json);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(999, 3)]
    public async Task List_MaxResultsIsClamped(int max, int expected)
    {
        await SeedThree();

        var result = await Run(_list, $$"""{"max_results":{{max}}}""");

        Assert.False(result.IsError);
        Assert.Equal(expected, ReminderJson.Parse(result.Content).GetArrayLength());
    }

    [Fact]
    public void List_Descriptor_IsReadOnly()
    {
        var d = _list.Descriptor;

        Assert.Equal("list_reminders", d.Name);
        Assert.Equal("列出提醒", d.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
    }

    // ---- cancel_reminder ----

    [Fact]
    public async Task Cancel_PendingItem_ReturnsCancelled()
    {
        var id = await CreateOneTime("開會");

        var result = await Run(_cancel, $$"""{"id":"{{id}}"}""");

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal(id, json.GetProperty("id").GetString());
        Assert.Equal("cancelled", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("cancelled").GetBoolean());
        Assert.Equal("已取消「開會」", result.Summary);
        var stored = await _rig.Store.GetAsync(id, default);
        Assert.Equal(ScheduledStatus.Cancelled, stored!.Status);
    }

    [Fact]
    public async Task Cancel_AlreadyCancelled_IsOkAndReportsStatus()
    {
        var id = await CreateOneTime();
        await Run(_cancel, $$"""{"id":"{{id}}"}""");

        var result = await Run(_cancel, $$"""{"id":"{{id}}"}""");

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal("cancelled", json.GetProperty("status").GetString());
        Assert.Equal("項目目前狀態為 cancelled，未進行變更。", json.GetProperty("message").GetString());
        Assert.False(json.TryGetProperty("cancelled", out _));
    }

    [Fact]
    public async Task Cancel_CompletedTimer_IsOkAndReportsCompleted()
    {
        var id = await StartTimer(600);
        await AdvanceAndProcess(Now.AddSeconds(601));

        var result = await Run(_cancel, $$"""{"id":"{{id}}"}""");

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal("completed", json.GetProperty("status").GetString());
        Assert.Equal("項目目前狀態為 completed，未進行變更。", json.GetProperty("message").GetString());
        Assert.Equal(ScheduledStatus.Completed, (await _rig.Store.GetAsync(id, default))!.Status);
    }

    [Fact]
    public async Task Cancel_UnknownId_IsError()
    {
        var result = await Run(_cancel, """{"id":"deadbeef"}""");

        Assert.True(result.IsError);
        Assert.Equal("找不到指定的提醒項目。", result.Content);
    }

    [Theory]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":"   "}""")]
    public async Task Cancel_BlankId_IsError(string json)
    {
        var result = await Run(_cancel, json);

        Assert.True(result.IsError);
        Assert.Contains("id 不能為空", result.Content);
    }

    [Fact]
    public void Cancel_Preview_ShowsId()
    {
        Assert.Equal("3f9a0c1e", _cancel.Preview("""{"id":" 3f9a0c1e "}""", _rig.Context).Target);
    }

    [Fact]
    public void Cancel_Descriptor()
    {
        var d = _cancel.Descriptor;

        Assert.Equal("cancel_reminder", d.Name);
        Assert.Equal("取消提醒", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
    }

    // ---- set_reminder_enabled ----

    [Fact]
    public async Task Disable_ThenEnable_OneTime_KeepsSameTime()
    {
        var id = await CreateOneTime("開會");

        var disabled = await Run(_enable, $$"""{"id":"{{id}}","enabled":false}""");
        var disabledJson = ReminderJson.Parse(disabled.Content);
        Assert.False(disabled.IsError);
        Assert.False(disabledJson.GetProperty("enabled").GetBoolean());
        Assert.Equal("已停用，後續將不會再觸發。", disabledJson.GetProperty("message").GetString());
        Assert.Equal("已停用「開會」", disabled.Summary);
        Assert.False((await _rig.Store.GetAsync(id, default))!.Enabled);

        var enabled = await Run(_enable, $$"""{"id":"{{id}}","enabled":true}""");
        var enabledJson = ReminderJson.Parse(enabled.Content);
        Assert.False(enabled.IsError);
        Assert.True(enabledJson.GetProperty("enabled").GetBoolean());
        Assert.False(enabledJson.TryGetProperty("message", out _));
        Assert.Equal("2026-10-25T09:00:00.0000000+08:00", enabledJson.GetProperty("next_fire_at").GetString());
        Assert.Equal("已啟用「開會」", enabled.Summary);
    }

    [Fact]
    public async Task Enable_OneTimeWhoseTimeHasPassed_IsErrorFromService()
    {
        var id = await CreateOneTime("開會");
        await Run(_enable, $$"""{"id":"{{id}}","enabled":false}""");
        _rig.Clock.SetNow(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8)));

        var result = await Run(_enable, $$"""{"id":"{{id}}","enabled":true}""");

        Assert.True(result.IsError);
        Assert.Contains("已經過了，無法啟用", result.Content);
    }

    [Fact]
    public async Task Enable_CompletedItem_IsError()
    {
        var id = await StartTimer(600);
        await AdvanceAndProcess(Now.AddSeconds(601));

        var result = await Run(_enable, $$"""{"id":"{{id}}","enabled":true}""");

        Assert.True(result.IsError);
        Assert.Contains("這個項目已經結束", result.Content);
    }

    [Fact]
    public async Task SetEnabled_UnknownId_IsError()
    {
        var result = await Run(_enable, """{"id":"deadbeef","enabled":false}""");

        Assert.True(result.IsError);
        Assert.Equal("找不到指定的提醒項目。", result.Content);
    }

    [Fact]
    public async Task SetEnabled_MissingEnabled_IsArgumentError()
    {
        var id = await CreateOneTime();

        var result = await Run(_enable, $$"""{"id":"{{id}}"}""");

        Assert.True(result.IsError);
        Assert.True((await _rig.Store.GetAsync(id, default))!.Enabled);   // 缺少參數不會默默停用
    }

    [Fact]
    public void SetEnabled_Descriptor()
    {
        var d = _enable.Descriptor;

        Assert.Equal("set_reminder_enabled", d.Name);
        Assert.Equal("啟用或停用提醒", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
        Assert.Contains("不會刪除", d.Description);
    }
}
