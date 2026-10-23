using System.Globalization;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Tools.Reminders;

namespace Lumi.Tools.Tests.Reminders;

/// <summary>M10 10E1：<c>create_reminder</c> 的參數解析、錯誤訊息、回傳格式與確認卡片。</summary>
public sealed class CreateReminderToolTests
{
    // 2026-10-23（週五）09:05 台北
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 5, 0, TimeSpan.Zero);

    private readonly ReminderRig _rig = new(Now);
    private readonly CreateReminderTool _tool;

    public CreateReminderToolTests()
    {
        _tool = new CreateReminderTool(_rig.Service, _rig.Options);
    }

    private Task<ToolResult> Run(string json) => _tool.ExecuteAsync(json, _rig.Context, default);

    private static string O(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Taipei(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    // ---- 一次性（at） ----

    [Theory]
    [InlineData("2026-10-25T09:00:00+08:00")]
    [InlineData("2026-10-25T01:00:00Z")]
    [InlineData("2026-10-25T09:00+08:00")]
    public async Task OneTime_WithOffset_UsesTheInstant(string at)
    {
        var result = await Run($$"""{"title":"開會","message":"準備投影片","at":"{{at}}","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal(O(Taipei(2026, 10, 25, 9, 0)), json.GetProperty("next_fire_at").GetString());
        Assert.Equal("2026-10-25（週日）09:00", json.GetProperty("next_fire_at_local").GetString());
        Assert.Equal("開會", json.GetProperty("title").GetString());
        Assert.False(json.TryGetProperty("recurrence", out _));
        Assert.Equal("已設定提醒 · 10/25（週日）09:00", result.Summary);
    }

    [Theory]
    [InlineData("2026-10-25T09:00")]
    [InlineData("2026-10-25 09:00")]
    [InlineData("2026-10-25T09:00:00")]
    public async Task OneTime_WithoutOffset_IsLocalTimeInTheZone(string at)
    {
        var result = await Run($$"""{"title":"開會","message":"m","at":"{{at}}","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError);
        Assert.Equal(O(Taipei(2026, 10, 25, 9, 0)), ReminderJson.Parse(result.Content).GetProperty("next_fire_at").GetString());
    }

    [Fact]
    public async Task OneTime_WithoutOffset_InNewYork_UsesThatZoneOffset()
    {
        var result = await Run("""{"title":"開會","message":"m","at":"2026-10-25T09:00","time_zone":"America/New_York"}""");

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal("2026-10-25T09:00:00.0000000-04:00", json.GetProperty("next_fire_at").GetString());
        Assert.Equal("2026-10-25（週日）09:00", json.GetProperty("next_fire_at_local").GetString());
    }

    [Fact]
    public async Task OneTime_SpringForwardGap_SlidesToFirstValidTime()
    {
        // 2026-03-08 美東春季前進：02:00 → 03:00，02:30 不存在，順延到 03:00 EDT
        var rig = new ReminderRig(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var tool = new CreateReminderTool(rig.Service, rig.Options);

        var result = await tool.ExecuteAsync("""{"title":"開會","message":"m","at":"2026-03-08T02:30","time_zone":"America/New_York"}""", rig.Context, default);

        Assert.False(result.IsError);
        Assert.Equal("2026-03-08T03:00:00.0000000-04:00", ReminderJson.Parse(result.Content).GetProperty("next_fire_at").GetString());
    }

    [Fact]
    public async Task OneTime_FallBackRepeat_TakesTheEarlierOccurrence()
    {
        // 2026-11-01 美東秋季後退：01:00–02:00 出現兩次，取較早的那次（EDT，-04:00）
        var rig = new ReminderRig(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
        var tool = new CreateReminderTool(rig.Service, rig.Options);

        var result = await tool.ExecuteAsync("""{"title":"開會","message":"m","at":"2026-11-01T01:30","time_zone":"America/New_York"}""", rig.Context, default);

        Assert.False(result.IsError);
        Assert.Equal("2026-11-01T01:30:00.0000000-04:00", ReminderJson.Parse(result.Content).GetProperty("next_fire_at").GetString());
    }

    [Fact]
    public async Task OneTime_NoZone_StoresTheLocalZoneId()
    {
        var result = await Run("""{"title":"開會","message":"m","at":"2026-10-25T09:00:00+08:00"}""");

        Assert.False(result.IsError);
        var stored = await _rig.Store.ListAsync(new ScheduledItemFilter(), default);
        Assert.Equal(TimeZoneInfo.Local.Id, Assert.Single(stored).TimeZoneId);
    }

    [Fact]
    public async Task OneTime_StoredAsReminderKind_WithOneTimeTrigger()
    {
        await Run("""{"title":"開會","message":"m","at":"2026-10-25T09:00:00+08:00","time_zone":"Asia/Taipei"}""");

        var stored = Assert.Single(await _rig.Store.ListAsync(new ScheduledItemFilter(), default));
        Assert.Equal(ScheduledKind.Reminder, stored.Kind);
        Assert.Equal(new OneTimeTrigger(Taipei(2026, 10, 25, 9, 0)), stored.Trigger);
        Assert.Equal(ScheduledCreator.Agent, stored.CreatedBy);
    }

    [Fact]
    public async Task OneTime_PastTime_IsErrorFromService()
    {
        var result = await Run("""{"title":"開會","message":"m","at":"2026-10-01T09:00:00+08:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("提醒時間不能是過去的時間", result.Content);
        Assert.Empty(await _rig.Store.ListAsync(new ScheduledItemFilter(), default));
    }

    [Theory]
    [InlineData("2026/10/25 09:00")]
    [InlineData("10/25 09:00")]
    [InlineData("2026-10-25")]
    [InlineData("明天早上九點")]
    public async Task OneTime_BadFormat_IsErrorWithExample(string at)
    {
        var result = await Run($$"""{"title":"開會","message":"m","at":"{{at}}","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("時間格式無效", result.Content);
        Assert.Contains("2026-10-05T15:30:00+08:00", result.Content);
    }

    // ---- at 與 recurrence 擇一 ----

    [Fact]
    public async Task NeitherAtNorRecurrence_IsError()
    {
        var result = await Run("""{"title":"開會","message":"m"}""");

        Assert.True(result.IsError);
        Assert.Contains("只能擇一", result.Content);
    }

    [Fact]
    public async Task BothAtAndRecurrence_IsError()
    {
        var result = await Run("""{"title":"開會","message":"m","at":"2026-10-25T09:00","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("只能擇一", result.Content);
        Assert.Empty(await _rig.Store.ListAsync(new ScheduledItemFilter(), default));
    }

    [Fact]
    public async Task UnknownTimeZone_IsError()
    {
        var result = await Run("""{"title":"開會","message":"m","at":"2026-10-25T09:00","time_zone":"Mars/Olympus"}""");

        Assert.True(result.IsError);
        Assert.Contains("Mars/Olympus", result.Content);
        Assert.Contains("Asia/Taipei", result.Content);
    }

    // ---- 標題與內容 ----

    [Theory]
    [InlineData("""{"title":"","message":"m","at":"2026-10-25T09:00"}""", "title 不可為空")]
    [InlineData("""{"title":"   ","message":"m","at":"2026-10-25T09:00"}""", "title 不可為空")]
    [InlineData("""{"title":"t","message":"","at":"2026-10-25T09:00"}""", "message 不可為空")]
    [InlineData("""{"title":"t","message":" \n ","at":"2026-10-25T09:00"}""", "message 不可為空")]
    public async Task BlankTitleOrMessage_IsError(string json, string expected)
    {
        var result = await Run(json);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
    }

    [Fact]
    public async Task TitleAndMessageLengthLimits()
    {
        string At(int title, int message) =>
            $$"""{"title":"{{new string('題', title)}}","message":"{{new string('字', message)}}","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""";

        var atLimit = await Run(At(100, 500));
        var longTitle = await Run(At(101, 1));
        var longMessage = await Run(At(1, 501));

        Assert.False(atLimit.IsError);
        Assert.True(longTitle.IsError);
        Assert.Contains("title 太長（上限 100 字）", longTitle.Content);
        Assert.True(longMessage.IsError);
        Assert.Contains("message 太長（上限 500 字）", longMessage.Content);
    }

    [Fact]
    public async Task TitleIsTrimmed()
    {
        var result = await Run("""{"title":"  開會  ","message":"m","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.Equal("開會", ReminderJson.Parse(result.Content).GetProperty("title").GetString());
    }

    [Fact]
    public async Task TooManyPendingItems_IsErrorFromService()
    {
        for (var i = 0; i < _rig.Options.MaxPendingItems; i++)
        {
            var ok = await Run("""{"title":"t","message":"m","at":"2026-11-01T09:00","time_zone":"Asia/Taipei"}""");
            Assert.False(ok.IsError);
        }

        var result = await Run("""{"title":"t","message":"m","at":"2026-11-01T09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("已達上限 (200 筆)", result.Content);
    }

    // ---- 週期（recurrence） ----

    [Fact]
    public async Task Recurring_Weekly_ReturnsDescriptionAndNextThreeOccurrences()
    {
        var result = await Run("""{"title":"喝水","message":"m","recurrence":"weekly","days":["monday","friday"],"time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal(O(Taipei(2026, 10, 26, 9, 0)), json.GetProperty("next_fire_at").GetString());
        Assert.Equal("每週一、五 09:00", json.GetProperty("recurrence").GetString());
        var occurrences = json.GetProperty("next_occurrences").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["2026-10-26（週一）09:00", "2026-10-30（週五）09:00", "2026-11-02（週一）09:00"], occurrences);
        Assert.Equal("已設定提醒 · 10/26（週一）09:00", result.Summary);
    }

    [Fact]
    public async Task Recurring_StoredAsScheduleKind_WithRecurrenceTrigger()
    {
        await Run("""{"title":"喝水","message":"m","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""");

        var stored = Assert.Single(await _rig.Store.ListAsync(new ScheduledItemFilter(), default));
        Assert.Equal(ScheduledKind.Schedule, stored.Kind);
        var trigger = Assert.IsType<RecurrenceTrigger>(stored.Trigger);
        Assert.Equal(RecurrenceKind.Daily, trigger.Kind);
        Assert.Equal(new TimeOnly(9, 0), trigger.TimeOfDay);
    }

    [Theory]
    [InlineData("daily", "每天 09:00")]
    [InlineData("DAILY", "每天 09:00")]
    [InlineData(" weekdays ", "每平日 09:00")]
    [InlineData("monthly", "每月 1 日 09:00")]
    public async Task Recurring_KindsAreCaseInsensitiveAndDescribed(string recurrence, string expected)
    {
        var result = await Run($$"""{"title":"t","message":"m","recurrence":"{{recurrence}}","time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError);
        Assert.Equal(expected, ReminderJson.Parse(result.Content).GetProperty("recurrence").GetString());
    }

    [Fact]
    public async Task Recurring_Monthly_DayOfMonthBeyondShortMonth_UsesLastDay()
    {
        var rig = new ReminderRig(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var tool = new CreateReminderTool(rig.Service, rig.Options);

        var result = await tool.ExecuteAsync("""{"title":"繳費","message":"m","recurrence":"monthly","day_of_month":31,"time":"09:00","time_zone":"Asia/Taipei"}""", rig.Context, default);

        Assert.False(result.IsError);
        var json = ReminderJson.Parse(result.Content);
        Assert.Equal(O(Taipei(2026, 2, 28, 9, 0)), json.GetProperty("next_fire_at").GetString());
        Assert.Equal("每月 31 日 09:00", json.GetProperty("recurrence").GetString());
    }

    [Theory]
    [InlineData("""{"recurrence":"weekly","time":"09:00"}""")]
    [InlineData("""{"recurrence":"weekly","time":"09:00","days":[]}""")]
    [InlineData("""{"recurrence":"weekly","time":"09:00","days":["funday"]}""")]
    public async Task Weekly_WithoutValidDays_IsError(string extra)
    {
        var json = extra.TrimEnd('}') + ",\"title\":\"t\",\"message\":\"m\",\"time_zone\":\"Asia/Taipei\"}";

        var result = await Run(json);

        Assert.True(result.IsError);
        Assert.Contains("必須指定至少一個有效的星期幾", result.Content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public async Task Monthly_DayOfMonthOutOfRange_IsError(int day)
    {
        var result = await Run($$"""{"title":"t","message":"m","recurrence":"monthly","day_of_month":{{day}},"time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("day_of_month 必須介於 1 到 31 之間", result.Content);
    }

    [Fact]
    public async Task Recurring_MissingTime_IsError()
    {
        var result = await Run("""{"title":"t","message":"m","recurrence":"daily","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("缺少必填參數 time", result.Content);
    }

    [Theory]
    [InlineData("9:00")]
    [InlineData("24:00")]
    [InlineData("0900")]
    [InlineData("上午九點")]
    public async Task Recurring_BadTimeFormat_IsError(string time)
    {
        var result = await Run($$"""{"title":"t","message":"m","recurrence":"daily","time":"{{time}}","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("時間格式無效，請使用 HH:mm 格式", result.Content);
    }

    [Fact]
    public async Task Recurring_UnknownKind_IsError()
    {
        var result = await Run("""{"title":"t","message":"m","recurrence":"hourly","time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("未知的 recurrence 類型，僅支援 daily, weekdays, weekly, monthly", result.Content);
    }

    [Theory]
    [InlineData("""["Monday","FRIDAY","1","5","5"]""", "每週一、五 09:00")]
    [InlineData("""["0"]""", "每週日 09:00")]
    [InlineData("""["7","sunday"]""", "每週日 09:00")]
    [InlineData("""["monday","bogus"]""", "每週一 09:00")]
    [InlineData("""["wednesday","tuesday"]""", "每週二、三 09:00")]
    public async Task Weekly_DaysAcceptNamesAndNumbers_DedupedAndMondayFirst(string days, string expected)
    {
        var result = await Run($$"""{"title":"t","message":"m","recurrence":"weekly","days":{{days}},"time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError);
        Assert.Equal(expected, ReminderJson.Parse(result.Content).GetProperty("recurrence").GetString());
    }

    [Theory]
    [InlineData("""["monday", 5]""", "每週一、五 09:00")]
    [InlineData("""[1,5]""", "每週一、五 09:00")]
    [InlineData("""[0, 7]""", "每週日 09:00")]
    [InlineData("""["friday", 1, "1"]""", "每週一、五 09:00")]
    public async Task Weekly_DaysAcceptNumbersInJson_ToleratedAsStrings(string days, string expected)
    {
        var result = await Run($$"""{"title":"t","message":"m","recurrence":"weekly","days":{{days}},"time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal(expected, ReminderJson.Parse(result.Content).GetProperty("recurrence").GetString());
    }

    [Fact]
    public async Task Weekly_DaysWithNonIntegerNumber_IsArgumentError()
    {
        var result = await Run("""{"title":"t","message":"m","recurrence":"weekly","days":[1.5],"time":"09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("參數不正確", result.Content);
    }

    [Fact]
    public void Days_SchemaStaysAnArrayOfStrings()
    {
        var days = _tool.InputSchema["properties"]!["days"]!.AsObject();

        // 可為 null 的陣列：type 是 ["array","null"]；元素型別仍是字串
        Assert.Contains("array", days["type"]!.ToJsonString());
        Assert.Equal("string", days["items"]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(days["description"]?.GetValue<string>()));
    }

    // ---- 確認卡片（Describe） ----

    [Fact]
    public void Preview_OneTime_ShowsTitleAndLocalTimeWithWeekdayAndZone()
    {
        var preview = _tool.Preview("""{"title":"  開會 ","message":"m","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""", _rig.Context);

        Assert.Equal("開會", preview.Target);
        Assert.Equal("2026-10-25（週日）09:00 · Asia/Taipei", preview.Detail);
    }

    [Fact]
    public void Preview_OneTime_InNewYork_ShowsLocalTimeOfThatZone()
    {
        var preview = _tool.Preview("""{"title":"開會","message":"m","at":"2026-10-25T09:00+08:00","time_zone":"America/New_York"}""", _rig.Context);

        Assert.Equal("2026-10-24（週六）21:00 · America/New_York", preview.Detail);
    }

    [Fact]
    public void Preview_Weekly_ShowsDescriptionAndNextThreeWithWeekdays()
    {
        var preview = _tool.Preview("""{"title":"喝水","message":"m","recurrence":"weekly","days":["monday","friday"],"time":"09:00","time_zone":"Asia/Taipei"}""", _rig.Context);

        Assert.Equal("喝水", preview.Target);
        Assert.Equal("每週一、五 09:00，接下來：2026-10-26（週一）09:00、2026-10-30（週五）09:00、2026-11-02（週一）09:00", preview.Detail);
    }

    [Fact]
    public void Preview_Weekdays_ShowsPlainWeekdayDescription()
    {
        var preview = _tool.Preview("""{"title":"喝水","message":"m","recurrence":"weekdays","time":"09:00","time_zone":"Asia/Taipei"}""", _rig.Context);

        Assert.StartsWith("每平日 09:00，接下來：2026-10-26（週一）09:00", preview.Detail);
    }

    [Theory]
    [InlineData("""{"title":"開會","message":"m","at":"not a time","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"title":"開會","message":"m","at":"2026-10-25T09:00","time_zone":"Mars/Olympus"}""")]
    [InlineData("""{"title":"開會","message":"m"}""")]
    public void Preview_Unparseable_ShowsTitleWithoutDetail(string json)
    {
        var preview = _tool.Preview(json, _rig.Context);

        Assert.Equal("開會", preview.Target);
        Assert.Null(preview.Detail);
    }

    [Fact]
    public async Task Preview_DoesNotCreateAnything()
    {
        _tool.Preview("""{"title":"開會","message":"m","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""", _rig.Context);

        Assert.Empty(await _rig.Store.ListAsync(new ScheduledItemFilter(), default));
    }

    // ---- 描述符與說明 ----

    [Fact]
    public void Descriptor_IsWriteBothWithTenSecondTimeout_AndNoScope()
    {
        var d = _tool.Descriptor;

        Assert.Equal("create_reminder", d.Name);
        Assert.Equal("建立提醒", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
        Assert.False(d.RequiresScope);
    }

    [Fact]
    public void Description_TellsModelToUseGetCurrentTimeAndNotPromiseTheTime()
    {
        var description = _tool.Descriptor.Description;

        Assert.Contains("get_current_time", description);
        Assert.Contains("沒有在執行", description);
        Assert.Contains("不要承諾", description);
        Assert.Contains("recurrence", description);
    }
}
