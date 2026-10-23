using Lumi.Core.Reminders;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Reminders;

/// <summary>到點觸發、錯過的規則（啟動與睡眠喚醒）、清除與等待時間。</summary>
public sealed class ReminderServiceFireTests
{
    private static DateTimeOffset T(string text) => ReminderTestData.T(text);

    [Fact]
    public async Task ProcessDue_OneTime_FiresOnceAndCompletes()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));

        await h.Service.ProcessDueAsync(default);
        await h.Service.ProcessDueAsync(default);

        var notification = Assert.Single(h.Sink.Shown);
        Assert.Equal(NotificationKind.Reminder, notification.Kind);
        Assert.Equal("吃藥", notification.Title);
        Assert.Equal("飯後吃藥。", notification.Message);
        var stored = (await h.Store.GetAsync(item.Id))!;
        Assert.Equal(ScheduledStatus.Completed, stored.Status);
        Assert.Equal(T("2026-10-23T09:00+08:00"), stored.LastFiredAt);
    }

    [Fact]
    public async Task ProcessDue_NotYetDue_DoesNothing()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        h.Clock.SetNow(T("2026-10-23T08:59:59+08:00"));

        await h.Service.ProcessDueAsync(default);

        Assert.Empty(h.Sink.Shown);
        Assert.Equal(ScheduledStatus.Pending, (await h.Store.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task ProcessDue_Recurrence_AdvancesToNextOccurrence_AndKeepsPending()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.Daily("09:00"), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));

        await h.Service.ProcessDueAsync(default);

        Assert.Single(h.Sink.Shown);
        var stored = (await h.Store.GetAsync(item.Id))!;
        Assert.Equal(ScheduledStatus.Pending, stored.Status);
        Assert.Equal(T("2026-10-24T09:00+08:00"), stored.NextFireAt);
        Assert.Equal(T("2026-10-23T09:00+08:00"), stored.LastFiredAt);
    }

    [Fact]
    public async Task ProcessDue_WritesStateBeforeNotifying()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));
        ScheduledStatus? seenByNotification = null;
        h.Sink.OnShowing = async _ => seenByNotification = (await h.Store.GetAsync(item.Id))!.Status;

        await h.Service.ProcessDueAsync(default);

        Assert.Equal(ScheduledStatus.Completed, seenByNotification);
    }

    [Fact]
    public async Task ProcessDue_SinkFailure_DoesNotAffectOtherItemsOrNextRound()
    {
        var h = new ReminderHarness();
        var first = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "甲"), default);
        var second = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "乙"), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));
        h.Sink.FailuresRemaining = 1;

        await h.Service.ProcessDueAsync(default);

        // 兩筆都處理過（同一時間，哪一筆先處理不確定）：其中一則顯示失敗，另一則照常送出；失敗的那一筆狀態也已寫入，不會重響
        Assert.Single(h.Sink.Shown);
        Assert.Equal(ScheduledStatus.Completed, (await h.Store.GetAsync(first.Id))!.Status);
        Assert.Equal(ScheduledStatus.Completed, (await h.Store.GetAsync(second.Id))!.Status);

        await h.Service.ProcessDueAsync(default);
        Assert.Single(h.Sink.Shown);
    }

    [Fact]
    public async Task ProcessDue_DisabledItem_IsNotFired()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        await h.Service.SetEnabledAsync(item.Id, false, default);
        h.Clock.SetNow(T("2026-10-23T09:05+08:00"));

        await h.Service.ProcessDueAsync(default);

        Assert.Empty(h.Sink.Shown);
        Assert.Equal(ScheduledStatus.Pending, (await h.Store.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task ProcessDue_SeveralItemsAtOnce_AllFire()
    {
        var h = new ReminderHarness();
        foreach (var title in new[] { "甲", "乙", "丙" })
            await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: title), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));

        await h.Service.ProcessDueAsync(default);

        Assert.Equal(3, h.Sink.Shown.Count);
    }

    [Fact]
    public async Task Startup_OneTimeWithinGrace_FiresNormally()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        h.Clock.SetNow(T("2026-10-23T09:01+08:00"));

        await h.Service.OnStartupAsync(default);

        Assert.Equal(NotificationKind.Reminder, Assert.Single(h.Sink.Shown).Kind);
        Assert.Equal(ScheduledStatus.Completed, (await h.Store.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task Startup_OneTimeBeyondGrace_IsMissedAndSummarized_WithoutReminder()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "吃藥"), default);
        h.Clock.SetNow(T("2026-10-23T09:03+08:00"));

        await h.Service.OnStartupAsync(default);

        Assert.Equal(ScheduledStatus.Missed, (await h.Store.GetAsync(item.Id))!.Status);
        var summary = Assert.Single(h.Sink.Shown);
        Assert.Equal(NotificationKind.MissedSummary, summary.Kind);
        Assert.Equal("你錯過了 1 個提醒", summary.Title);
        Assert.Equal("・吃藥", summary.Message);
    }

    [Fact]
    public async Task Startup_SummaryListsAtMostFiveTitles_ThenTheRemainder()
    {
        var h = new ReminderHarness();
        // 觸發時間各不相同，彙總的順序才確定（同一時間的依 Id 排）
        for (var i = 1; i <= 7; i++)
            await h.Service.CreateReminderAsync(ReminderTestData.OneTime($"2026-10-23T09:0{i}+08:00", title: $"項目{i}"), default);
        h.Clock.SetNow(T("2026-10-23T09:10+08:00"));

        await h.Service.OnStartupAsync(default);

        var summary = Assert.Single(h.Sink.Shown);
        Assert.Equal("你錯過了 7 個提醒", summary.Title);
        Assert.Equal(
            "・項目1\n・項目2\n・項目3\n・項目4\n・項目5\n…另外還有 2 個",
            summary.Message);
    }

    [Fact]
    public async Task Startup_MissedRecurrence_FiresOnceAndAdvancesToTheFuture()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.Daily("09:00"), default);
        h.Clock.SetNow(T("2026-10-26T10:00+08:00"));

        await h.Service.OnStartupAsync(default);

        var notification = Assert.Single(h.Sink.Shown);
        Assert.Equal(NotificationKind.Reminder, notification.Kind);
        var stored = (await h.Store.GetAsync(item.Id))!;
        Assert.Equal(ScheduledStatus.Pending, stored.Status);
        Assert.Equal(T("2026-10-27T09:00+08:00"), stored.NextFireAt);
    }

    [Fact]
    public async Task Startup_AfterSleepHours_MissesOneTimeAndFiresRecurrenceOnce()
    {
        var h = new ReminderHarness();
        var meeting = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "會議"), default);
        var water = await h.Service.CreateReminderAsync(ReminderTestData.Daily("09:00", title: "喝水"), default);
        h.Clock.SetNow(T("2026-10-23T12:00+08:00"));   // 睡眠了三個小時

        await h.Service.OnStartupAsync(default);

        Assert.Equal(ScheduledStatus.Missed, (await h.Store.GetAsync(meeting.Id))!.Status);
        Assert.Equal(T("2026-10-24T09:00+08:00"), (await h.Store.GetAsync(water.Id))!.NextFireAt);
        Assert.Equal(2, h.Sink.Shown.Count);
        Assert.Equal("喝水", h.Sink.Shown.Single(n => n.Kind == NotificationKind.Reminder).Title);
        Assert.Equal("・會議", h.Sink.Shown.Single(n => n.Kind == NotificationKind.MissedSummary).Message);
    }

    [Fact]
    public async Task Startup_NothingMissed_ShowsNothing()
    {
        var h = new ReminderHarness();
        await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);

        await h.Service.OnStartupAsync(default);

        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public async Task Startup_PurgesFinishedItemsOlderThanRetention_KeepsRecentOnes()
    {
        var h = new ReminderHarness();
        var old = await h.Service.StartTimerAsync("舊的", 60, default);
        h.Clock.SetNow(old.NextFireAt!.Value);
        await h.Service.ProcessDueAsync(default);

        h.Clock.SetNow(T("2026-11-20T08:00+08:00"));
        var recent = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-11-20T09:00+08:00"), default);
        h.Clock.SetNow(T("2026-11-20T09:00+08:00"));
        await h.Service.ProcessDueAsync(default);

        h.Clock.SetNow(T("2026-12-01T00:00+08:00"));   // 保留 30 天：10/23 的已超過，11/20 的還在
        await h.Service.OnStartupAsync(default);

        Assert.Null(await h.Store.GetAsync(old.Id));
        Assert.NotNull(await h.Store.GetAsync(recent.Id));
    }

    [Fact]
    public async Task GetWait_NoItems_ReturnsNull()
    {
        var h = new ReminderHarness();

        Assert.Null(await h.Service.GetWaitAsync(default));
    }

    [Fact]
    public async Task GetWait_ItemInFiveSeconds_ReturnsFiveSeconds()
    {
        var h = new ReminderHarness();
        await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T08:00:05+08:00"), default);

        Assert.Equal(TimeSpan.FromSeconds(5), await h.Service.GetWaitAsync(default));
    }

    [Fact]
    public async Task GetWait_ItemFarAway_ReturnsMaxWait()
    {
        var h = new ReminderHarness();
        await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T10:00+08:00"), default);

        Assert.Equal(TimeSpan.FromSeconds(60), await h.Service.GetWaitAsync(default));
    }

    [Fact]
    public async Task GetWait_OverdueItem_ReturnsZero()
    {
        var h = new ReminderHarness();
        await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T08:00:30+08:00"), default);
        h.Clock.SetNow(T("2026-10-23T08:01+08:00"));

        Assert.Equal(TimeSpan.Zero, await h.Service.GetWaitAsync(default));
    }

    [Fact]
    public async Task GetWait_DisabledItemsAreIgnored()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T10:00+08:00"), default);
        await h.Service.SetEnabledAsync(item.Id, false, default);

        Assert.Null(await h.Service.GetWaitAsync(default));
    }
}
