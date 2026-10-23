using Lumi.Core.Reminders;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Reminders;

/// <summary>建立、驗證、上限、啟停、取消與變更訊號。觸發與錯過的規則見 <see cref="ReminderServiceFireTests"/>。</summary>
public sealed class ReminderServiceCreateTests
{
    private static DateTimeOffset T(string text) => ReminderTestData.T(text);

    [Fact]
    public async Task CreateOneTime_StoresPendingItem_WithAtAsNextFireAt()
    {
        var h = new ReminderHarness();

        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);

        Assert.Equal(ScheduledStatus.Pending, item.Status);
        Assert.True(item.Enabled);
        Assert.Equal(T("2026-10-23T09:00+08:00"), item.NextFireAt);
        Assert.Equal("Asia/Taipei", item.TimeZoneId);
        Assert.Equal(ScheduledCreator.User, item.CreatedBy);
        Assert.Equal(h.Clock.GetUtcNow(), item.CreatedAt);
        Assert.Equal(item, await h.Store.GetAsync(item.Id));
    }

    [Fact]
    public async Task CreateRecurrence_NextFireAtIsNextOccurrence()
    {
        var h = new ReminderHarness();

        var item = await h.Service.CreateReminderAsync(ReminderTestData.Daily("09:00"), default);

        Assert.Equal(T("2026-10-23T09:00+08:00"), item.NextFireAt);
        Assert.Equal(ScheduledKind.Schedule, item.Kind);
    }

    [Fact]
    public async Task Create_TrimsTitleAndMessage()
    {
        var h = new ReminderHarness();

        var item = await h.Service.CreateReminderAsync(
            ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "  吃藥  ", message: "\t飯後\n"), default);

        Assert.Equal("吃藥", item.Title);
        Assert.Equal("飯後", item.Message);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Create_BlankTitle_Throws(string title)
    {
        var h = new ReminderHarness();

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: title), default));

        Assert.Equal("標題不能是空的。", ex.Message);
    }

    [Fact]
    public async Task Create_BlankMessage_Throws()
    {
        var h = new ReminderHarness();

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", message: "  "), default));

        Assert.Equal("內容不能是空的。", ex.Message);
    }

    [Fact]
    public async Task Create_TitleLongerThanLimit_Throws()
    {
        var h = new ReminderHarness();
        var title = new string('字', 101);

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: title), default));

        Assert.Equal("標題不能超過 100 字。", ex.Message);
    }

    [Fact]
    public async Task Create_MessageLongerThanLimit_Throws()
    {
        var h = new ReminderHarness();
        var message = new string('字', 501);

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", message: message), default));

        Assert.Equal("內容不能超過 500 字。", ex.Message);
    }

    [Theory]
    [InlineData("2026-10-23T08:00+08:00")]   // 等於現在也不算晚於現在
    [InlineData("2026-10-23T07:00+08:00")]
    public async Task Create_OneTimeNotAfterNow_Throws(string at)
    {
        var h = new ReminderHarness();

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreateReminderAsync(ReminderTestData.OneTime(at), default));

        Assert.Equal("提醒時間不能是過去的時間。", ex.Message);
    }

    [Fact]
    public async Task Create_MonthlyDayOutOfRange_Throws()
    {
        var h = new ReminderHarness();
        var request = new NewReminder(
            "繳費", "記得繳費。",
            new RecurrenceTrigger(RecurrenceKind.Monthly, new TimeOnly(9, 0), null, 32),
            "Asia/Taipei", ScheduledKind.Schedule, ScheduledCreator.User);

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.CreateReminderAsync(request, default));

        Assert.Equal("每月的日期必須介於 1 到 31 之間。", ex.Message);
    }

    [Fact]
    public async Task Create_WithoutTimeZone_UsesLocalZone()
    {
        var h = new ReminderHarness();

        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", zone: null), default);

        Assert.Equal(ScheduleCalculator.ResolveTimeZone(string.Empty).Id, item.TimeZoneId);
    }

    [Fact]
    public async Task Create_AtPendingLimit_ThrowsUntilSomethingFinishes()
    {
        var h = new ReminderHarness();
        var ids = new List<string>();
        for (var i = 0; i < 200; i++)
            ids.Add((await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default)).Id);

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default));
        Assert.Equal("未完成排程項目已達上限 (200 筆)。", ex.Message);

        // 取消一筆之後又能建立
        await h.Service.CancelAsync(ids[0], default);
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        Assert.Equal(ScheduledStatus.Pending, item.Status);
    }

    [Fact]
    public async Task Create_ConcurrentRequests_NeverExceedTheLimit()
    {
        var h = new ReminderHarness();

        var results = await Task.WhenAll(Enumerable.Range(0, 250).Select(async _ =>
        {
            try
            {
                await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
                return true;
            }
            catch (ReminderException)
            {
                return false;
            }
        }));

        Assert.Equal(200, results.Count(ok => ok));
        Assert.Equal(200, await h.Store.CountPendingAsync());
    }

    [Fact]
    public async Task Create_WhenIdCollides_RetriesWithANewId()
    {
        var store = new CollidingScheduledItemStore(new InMemoryScheduledItemStore(), collisions: 1);
        var h = new ReminderHarness(store: store);

        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "新的"), default);

        Assert.Equal(2, store.AddAttempts);
        Assert.Equal("新的", (await h.Store.GetAsync(item.Id))!.Title);
        Assert.Equal(2, (await h.Store.ListAsync(new ScheduledItemFilter())).Count);
    }

    [Fact]
    public async Task Create_WhenIdCollidesEveryTime_GivesUpAfterFiveAttempts()
    {
        var store = new CollidingScheduledItemStore(new InMemoryScheduledItemStore(), collisions: 100);
        var h = new ReminderHarness(store: store);

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default));

        Assert.Equal("無法產生不重複的編號，請再試一次。", ex.Message);
        Assert.Equal(5, store.AddAttempts);
    }

    [Fact]
    public async Task StartTimer_CreatesTimerWithLabelAndMessage()
    {
        var h = new ReminderHarness();

        var item = await h.Service.StartTimerAsync("泡麵", 180, default);

        Assert.Equal(ScheduledKind.Timer, item.Kind);
        Assert.Equal("泡麵", item.Title);
        Assert.Equal("「泡麵」時間到了。", item.Message);
        Assert.Equal(ScheduledCreator.Agent, item.CreatedBy);
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(180), item.NextFireAt);
        Assert.Equal(new OneTimeTrigger(h.Clock.GetUtcNow().AddSeconds(180)), item.Trigger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task StartTimer_WithoutLabel_UsesDefaultName(string? label)
    {
        var h = new ReminderHarness();

        var item = await h.Service.StartTimerAsync(label, 60, default);

        Assert.Equal("計時器", item.Title);
        Assert.Equal("「計時器」時間到了。", item.Message);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(86_401)]
    public async Task StartTimer_DurationOutOfRange_Throws(int seconds)
    {
        var h = new ReminderHarness();

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.StartTimerAsync("計時", seconds, default));

        Assert.Equal("durationSeconds 必須介於 10 到 86400 秒 (24 小時) 之間。", ex.Message);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(86_400)]
    public async Task StartTimer_BoundaryDurations_AreAccepted(int seconds)
    {
        var h = new ReminderHarness();

        var item = await h.Service.StartTimerAsync("計時", seconds, default);

        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(seconds), item.NextFireAt);
    }

    [Fact]
    public async Task StartTimer_LabelLongerThanLimit_Throws()
    {
        var h = new ReminderHarness();

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.StartTimerAsync(new string('字', 101), 60, default));

        Assert.Equal("計時器名稱不能超過 100 字。", ex.Message);
    }

    [Fact]
    public async Task List_FiltersByStatusKindAndLimit_InFireOrder()
    {
        var h = new ReminderHarness();
        var reminder = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "A"), default);
        var cancelled = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T10:00+08:00", title: "B"), default);
        var timer = await h.Service.StartTimerAsync("茶", 600, default);   // 08:10，最早
        await h.Service.CancelAsync(cancelled.Id, default);

        var pending = await h.Service.ListAsync(new ReminderQuery([ScheduledStatus.Pending], null, 10), default);
        var done = await h.Service.ListAsync(new ReminderQuery([ScheduledStatus.Cancelled], null, 10), default);
        var timers = await h.Service.ListAsync(new ReminderQuery(null, [ScheduledKind.Timer], 10), default);
        var first = await h.Service.ListAsync(new ReminderQuery(null, null, 1), default);

        Assert.Equal(new[] { timer.Id, reminder.Id }, pending.Select(item => item.Id));
        Assert.Equal(new[] { cancelled.Id }, done.Select(item => item.Id));
        Assert.Equal(new[] { timer.Id }, timers.Select(item => item.Id));
        Assert.Single(first);
    }

    [Fact]
    public async Task List_LimitBelowOne_Throws()
    {
        var h = new ReminderHarness();

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.ListAsync(new ReminderQuery(null, null, 0), default));

        Assert.Equal("筆數必須至少是 1。", ex.Message);
    }

    [Fact]
    public async Task Cancel_Pending_BecomesCancelledAndKeepsEnabledFlag()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        await h.Service.SetEnabledAsync(item.Id, false, default);

        var result = await h.Service.CancelAsync(item.Id, default);

        Assert.True(result.Changed);
        Assert.Equal(ScheduledStatus.Cancelled, result.Item!.Status);
        Assert.False(result.Item.Enabled);
        Assert.Equal(ScheduledStatus.Cancelled, (await h.Store.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task Cancel_FinishedItem_IsNotChanged()
    {
        var h = new ReminderHarness();
        var timer = await h.Service.StartTimerAsync("茶", 60, default);
        h.Clock.SetNow(timer.NextFireAt!.Value);
        await h.Service.ProcessDueAsync(default);

        var result = await h.Service.CancelAsync(timer.Id, default);

        Assert.False(result.Changed);
        Assert.Equal(ScheduledStatus.Completed, result.Item!.Status);
    }

    [Fact]
    public async Task Cancel_UnknownId_ReturnsNullItem()
    {
        var h = new ReminderHarness();

        var result = await h.Service.CancelAsync("missing1", default);

        Assert.Null(result.Item);
        Assert.False(result.Changed);
    }

    [Fact]
    public async Task SetEnabled_EnablingRecomputesRecurrence_AfterTheTimePassed()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.Daily("09:00"), default);
        await h.Service.SetEnabledAsync(item.Id, false, default);
        h.Clock.SetNow(T("2026-10-23T10:00+08:00"));

        var enabled = await h.Service.SetEnabledAsync(item.Id, true, default);

        Assert.NotNull(enabled);
        Assert.True(enabled.Enabled);
        Assert.Equal(T("2026-10-24T09:00+08:00"), enabled.NextFireAt);
    }

    [Fact]
    public async Task SetEnabled_Disabling_KeepsNextFireAt()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);

        var disabled = await h.Service.SetEnabledAsync(item.Id, false, default);

        Assert.NotNull(disabled);
        Assert.False(disabled.Enabled);
        Assert.Equal(item.NextFireAt, disabled.NextFireAt);
        Assert.Equal(ScheduledStatus.Pending, disabled.Status);
    }

    [Fact]
    public async Task SetEnabled_EnablingOneTimeThatAlreadyPassed_Throws()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        await h.Service.SetEnabledAsync(item.Id, false, default);
        h.Clock.SetNow(T("2026-10-23T10:00+08:00"));

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.SetEnabledAsync(item.Id, true, default));

        Assert.Equal("這個提醒的時間已經過了，無法啟用。", ex.Message);
    }

    [Fact]
    public async Task SetEnabled_UnknownId_ReturnsNull()
    {
        var h = new ReminderHarness();

        Assert.Null(await h.Service.SetEnabledAsync("missing1", true, default));
    }

    [Fact]
    public async Task SetEnabled_FinishedItem_Throws()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        await h.Service.CancelAsync(item.Id, default);

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.SetEnabledAsync(item.Id, true, default));

        Assert.Equal("這個項目已經結束，無法啟用或停用。", ex.Message);
    }

    [Fact]
    public async Task ChangeToken_IsSignaled_ByCreateCancelAndSetEnabled()
    {
        var h = new ReminderHarness();

        var before = h.Service.ChangeToken;
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        Assert.True(before.IsCancellationRequested);

        var afterCreate = h.Service.ChangeToken;
        Assert.False(afterCreate.IsCancellationRequested);
        await h.Service.SetEnabledAsync(item.Id, false, default);
        Assert.True(afterCreate.IsCancellationRequested);

        var afterDisable = h.Service.ChangeToken;
        await h.Service.CancelAsync(item.Id, default);
        Assert.True(afterDisable.IsCancellationRequested);
    }
}
