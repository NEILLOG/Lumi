using Lumi.Core.Context;
using Lumi.Core.Reminders;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Reminders;

/// <summary>
/// 「跑 prompt」的排程（M24）：建立與修改的驗證、到點與補跑時交給執行器、錯過的規則。
/// 執行器本身見 <see cref="ScheduledRunServiceTests"/>；這裡用記錄用的假執行器。
/// </summary>
public sealed class ReminderServicePromptTests
{
    private static DateTimeOffset T(string text) => ReminderTestData.T(text);

    private static readonly ScheduleGrant NoGrant = ScheduleGrant.None;

    private static NewPromptSchedule Prompt(
        string name = "整理更新", string prompt = "把更新整理成筆記。", Trigger? trigger = null, ScheduleGrant? grant = null,
        IReadOnlyList<ScheduleScopeItem>? scope = null) =>
        new(name, prompt,
            trigger ?? new OneTimeTrigger(T("2026-10-23T09:00+08:00")),
            "Asia/Taipei", grant ?? NoGrant, scope ?? [], ScheduledCreator.Agent);

    private static NewPromptSchedule DailyPrompt(string time = "09:00") =>
        Prompt(trigger: new RecurrenceTrigger(RecurrenceKind.Daily, TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture), null, null));

    [Fact]
    public async Task Create_StoresRunPromptAction_WithPromptGrantAndScope()
    {
        var h = new ReminderHarness();
        var scope = new[] { new ScheduleScopeItem(ContextKind.Folder, "/notes", "筆記"), new ScheduleScopeItem(ContextKind.File, "/a.txt", "a.txt") };

        var item = await h.Service.CreatePromptScheduleAsync(
            Prompt(name: "早上整理", grant: new ScheduleGrant(Write: true, Network: true, Destructive: false), scope: scope), default);

        Assert.Equal("早上整理", item.Title);
        Assert.Equal(ScheduledKind.Reminder, item.Kind);
        Assert.Equal(ScheduledStatus.Pending, item.Status);
        Assert.Equal(T("2026-10-23T09:00+08:00"), item.NextFireAt);
        var action = Assert.IsType<RunPromptAction>(item.Action);
        Assert.Equal("把更新整理成筆記。", action.Prompt);
        Assert.Equal(new ScheduleGrant(true, true, false), action.Grant);
        Assert.Equal(scope, action.Scope);
        Assert.Equal(item, await h.Store.GetAsync(item.Id));
    }

    [Fact]
    public async Task Create_Recurrence_IsKindSchedule()
    {
        var h = new ReminderHarness();

        var item = await h.Service.CreatePromptScheduleAsync(DailyPrompt(), default);

        Assert.Equal(ScheduledKind.Schedule, item.Kind);
        Assert.Equal(T("2026-10-23T09:00+08:00"), item.NextFireAt);
    }

    [Fact]
    public async Task Create_LongPrompt_KeepsOnly200CharsInMessage_AndFullPromptInAction()
    {
        var h = new ReminderHarness();
        var prompt = new string('字', 250);

        var item = await h.Service.CreatePromptScheduleAsync(Prompt(prompt: prompt), default);

        Assert.Equal(new string('字', 200) + "…", item.Message);
        Assert.Equal(prompt, Assert.IsType<RunPromptAction>(item.Action).Prompt);
    }

    [Fact]
    public async Task Create_ShortPrompt_MessageIsThePromptItself()
    {
        var h = new ReminderHarness();

        var item = await h.Service.CreatePromptScheduleAsync(Prompt(prompt: "  查天氣  "), default);

        Assert.Equal("查天氣", item.Message);
    }

    [Theory]
    [InlineData("   ", "把更新整理成筆記。", "名稱不能是空的。")]
    [InlineData("整理", "   ", "提示內容不能是空的。")]
    public async Task Create_BlankNameOrPrompt_Throws(string name, string prompt, string message)
    {
        var h = new ReminderHarness();

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.CreatePromptScheduleAsync(Prompt(name, prompt), default));

        Assert.Equal(message, ex.Message);
        Assert.Empty(await h.Store.ListAsync(new ScheduledItemFilter()));
    }

    [Fact]
    public async Task Create_PromptOverLimit_Throws()
    {
        var h = new ReminderHarness();
        var limit = h.Options.MaxPromptChars;

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.CreatePromptScheduleAsync(Prompt(prompt: new string('a', limit + 1)), default));

        Assert.Equal($"提示內容不能超過 {limit} 字。", ex.Message);
    }

    [Fact]
    public async Task Create_PromptAtLimit_IsAccepted()
    {
        var h = new ReminderHarness();
        var limit = h.Options.MaxPromptChars;

        var item = await h.Service.CreatePromptScheduleAsync(Prompt(prompt: new string('a', limit)), default);

        Assert.Equal(limit, Assert.IsType<RunPromptAction>(item.Action).Prompt.Length);
    }

    [Theory]
    [InlineData(ContextKind.Image)]
    [InlineData(ContextKind.ScheduledRun)]
    public async Task Create_ScopeOtherThanFileOrFolder_Throws(ContextKind kind)
    {
        var h = new ReminderHarness();
        var scope = new[] { new ScheduleScopeItem(kind, "x", "x") };

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.CreatePromptScheduleAsync(Prompt(scope: scope), default));

        Assert.Equal("排程的工作範圍只能是檔案或資料夾。", ex.Message);
    }

    [Fact]
    public async Task Create_SignalsChange()
    {
        var h = new ReminderHarness();
        var token = h.Service.ChangeToken;

        await h.Service.CreatePromptScheduleAsync(Prompt(), default);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Create_AtPendingLimit_Throws()
    {
        var options = new ReminderOptions { MaxPendingItems = 1 };
        var h = new ReminderHarness(options);
        await h.Service.CreatePromptScheduleAsync(Prompt(), default);

        var ex = await Assert.ThrowsAsync<ReminderException>(() => h.Service.CreatePromptScheduleAsync(Prompt(), default));

        Assert.Equal("未完成排程項目已達上限 (1 筆)。", ex.Message);
    }

    [Fact]
    public async Task Update_ChangesOnlyTheGivenFields()
    {
        var h = new ReminderHarness();
        var scope = new[] { new ScheduleScopeItem(ContextKind.Folder, "/notes", "筆記") };
        var item = await h.Service.CreatePromptScheduleAsync(Prompt(grant: new ScheduleGrant(true, false, false), scope: scope), default);

        var updated = await h.Service.UpdatePromptScheduleAsync(item.Id, new PromptScheduleUpdate("新的內容", null, null), default);

        Assert.NotNull(updated);
        var action = Assert.IsType<RunPromptAction>(updated!.Action);
        Assert.Equal("新的內容", action.Prompt);
        Assert.Equal(new ScheduleGrant(true, false, false), action.Grant);
        Assert.Equal(scope, action.Scope);
        Assert.Equal("新的內容", updated.Message);
        Assert.Equal(item.NextFireAt, updated.NextFireAt);
        Assert.Equal(updated, await h.Store.GetAsync(item.Id));
    }

    [Fact]
    public async Task Update_GrantAndScope()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreatePromptScheduleAsync(Prompt(), default);
        var scope = new[] { new ScheduleScopeItem(ContextKind.File, "/a.txt", "a.txt") };

        var updated = await h.Service.UpdatePromptScheduleAsync(item.Id, new PromptScheduleUpdate(null, new ScheduleGrant(false, true, false), scope), default);

        var action = Assert.IsType<RunPromptAction>(updated!.Action);
        Assert.Equal(new ScheduleGrant(false, true, false), action.Grant);
        Assert.Equal(scope, action.Scope);
        Assert.Equal("把更新整理成筆記。", action.Prompt);
    }

    [Fact]
    public async Task Update_NotFound_ReturnsNull()
    {
        var h = new ReminderHarness();

        var updated = await h.Service.UpdatePromptScheduleAsync("missing", new PromptScheduleUpdate("x", null, null), default);

        Assert.Null(updated);
    }

    [Fact]
    public async Task Update_NotARunPromptItem_Throws()
    {
        var h = new ReminderHarness();
        var reminder = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);

        var ex = await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.UpdatePromptScheduleAsync(reminder.Id, new PromptScheduleUpdate("x", null, null), default));

        Assert.Equal("這個項目不是跑 prompt 的排程，不能用這個方式修改。", ex.Message);
    }

    [Fact]
    public async Task Update_BlankPrompt_Throws_AndKeepsOldPrompt()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreatePromptScheduleAsync(Prompt(), default);

        await Assert.ThrowsAsync<ReminderException>(() =>
            h.Service.UpdatePromptScheduleAsync(item.Id, new PromptScheduleUpdate("  ", null, null), default));

        Assert.Equal("把更新整理成筆記。", Assert.IsType<RunPromptAction>((await h.Store.GetAsync(item.Id))!.Action).Prompt);
    }

    [Fact]
    public async Task DueRunPrompt_IsEnqueuedWithPlannedTime_AndSendsNoReminderNotification()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        var item = await h.Service.CreatePromptScheduleAsync(DailyPrompt(), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));

        await h.Service.ProcessDueAsync(default);

        var enqueued = Assert.Single(dispatcher.Enqueued);
        Assert.Equal(item.Id, enqueued.Item.Id);
        Assert.Equal(T("2026-10-23T09:00+08:00"), enqueued.PlannedAt);
        Assert.Equal(ScheduledRunTrigger.Scheduled, enqueued.Trigger);
        Assert.Empty(h.Sink.Shown);
        var stored = (await h.Store.GetAsync(item.Id))!;
        Assert.Equal(T("2026-10-24T09:00+08:00"), stored.NextFireAt);
        Assert.Equal(ScheduledStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task DueOneTimeRunPrompt_IsEnqueuedAndCompletes()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        var item = await h.Service.CreatePromptScheduleAsync(Prompt(), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));

        await h.Service.ProcessDueAsync(default);

        Assert.Single(dispatcher.Enqueued);
        Assert.Equal(ScheduledStatus.Completed, (await h.Store.GetAsync(item.Id))!.Status);
        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public async Task NotifyAction_IsUnchanged_WhenADispatcherIsPresent()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        h.Clock.SetNow(T("2026-10-23T09:00+08:00"));

        await h.Service.ProcessDueAsync(default);

        Assert.Empty(dispatcher.Enqueued);
        Assert.Equal(NotificationKind.Reminder, Assert.Single(h.Sink.Shown).Kind);
    }

    [Fact]
    public async Task Startup_RunPromptWithinCatchUpGrace_IsEnqueuedAsCatchUp_EvenAnHourLate()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        var item = await h.Service.CreatePromptScheduleAsync(Prompt(), default);
        h.Clock.SetNow(T("2026-10-23T10:00+08:00"));

        await h.Service.OnStartupAsync(default);

        var enqueued = Assert.Single(dispatcher.Enqueued);
        Assert.Equal(ScheduledRunTrigger.CatchUp, enqueued.Trigger);
        Assert.Equal(T("2026-10-23T09:00+08:00"), enqueued.PlannedAt);
        Assert.Empty(dispatcher.Missed);
        Assert.Empty(h.Sink.Shown);
        Assert.Equal(ScheduledStatus.Completed, (await h.Store.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task Startup_RunPromptBeyondGrace_IsRecordedMissed_AndSummarized()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        var item = await h.Service.CreatePromptScheduleAsync(Prompt(name: "整理更新"), default);
        h.Clock.SetNow(T("2026-10-23T12:00+08:00"));

        await h.Service.OnStartupAsync(default);

        Assert.Empty(dispatcher.Enqueued);
        var missed = Assert.Single(dispatcher.Missed);
        Assert.Equal(item.Id, missed.Item.Id);
        Assert.Equal(T("2026-10-23T09:00+08:00"), missed.PlannedAt);
        Assert.Equal(ScheduledStatus.Missed, (await h.Store.GetAsync(item.Id))!.Status);
        var summary = Assert.Single(h.Sink.Shown);
        Assert.Equal(NotificationKind.MissedSummary, summary.Kind);
        Assert.Equal("你錯過了 1 個提醒或排程", summary.Title);
        Assert.Equal("・整理更新", summary.Message);
    }

    [Fact]
    public async Task Startup_RecurringRunPromptBeyondGrace_IsMissed_ButStaysPendingAndAdvances()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        var item = await h.Service.CreatePromptScheduleAsync(DailyPrompt(), default);
        h.Clock.SetNow(T("2026-10-23T12:00+08:00"));

        await h.Service.OnStartupAsync(default);

        Assert.Empty(dispatcher.Enqueued);
        Assert.Single(dispatcher.Missed);
        var stored = (await h.Store.GetAsync(item.Id))!;
        Assert.Equal(ScheduledStatus.Pending, stored.Status);
        Assert.Equal(T("2026-10-24T09:00+08:00"), stored.NextFireAt);
    }

    [Fact]
    public async Task Startup_MissedRunPromptAndMissedReminder_ShareOneSummary()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        await h.Service.CreatePromptScheduleAsync(Prompt(name: "整理"), default);
        await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T08:30+08:00", title: "吃藥"), default);
        h.Clock.SetNow(T("2026-10-23T12:00+08:00"));

        await h.Service.OnStartupAsync(default);

        var summary = Assert.Single(h.Sink.Shown);
        Assert.Equal("你錯過了 2 個提醒或排程", summary.Title);
        Assert.Equal("・吃藥\n・整理", summary.Message);
    }

    [Fact]
    public async Task Startup_SetEnabledFalse_RunPromptIsNotEnqueued()
    {
        var dispatcher = new RecordingDispatcher();
        var h = new ReminderHarness(dispatcher: dispatcher);
        var item = await h.Service.CreatePromptScheduleAsync(Prompt(), default);
        await h.Service.SetEnabledAsync(item.Id, false, default);
        h.Clock.SetNow(T("2026-10-23T10:00+08:00"));

        await h.Service.OnStartupAsync(default);

        Assert.Empty(dispatcher.Enqueued);
        Assert.Empty(dispatcher.Missed);
    }

    /// <summary>記錄執行器收到的呼叫（不執行）。</summary>
    private sealed class RecordingDispatcher : IScheduledRunDispatcher
    {
        public List<(ScheduledItem Item, DateTimeOffset PlannedAt, ScheduledRunTrigger Trigger)> Enqueued { get; } = [];

        public List<(ScheduledItem Item, DateTimeOffset PlannedAt)> Missed { get; } = [];

        public void Enqueue(ScheduledItem item, DateTimeOffset plannedAt, ScheduledRunTrigger trigger) =>
            Enqueued.Add((item, plannedAt, trigger));

        public Task RecordMissedAsync(ScheduledItem item, DateTimeOffset plannedAt, CancellationToken ct)
        {
            Missed.Add((item, plannedAt));
            return Task.CompletedTask;
        }
    }
}
