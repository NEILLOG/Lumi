using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>
/// M24 24E：歷史抽屜「排程」分頁的 ViewModel（不開視窗）：分組與排序、副標、未讀、狀態徽章、打開紀錄、組與紀錄的動作、
/// 已刪除排程的組、進行中不能刪、執行紀錄與提醒的變動即時更新、空狀態，以及「對話｜排程」分頁切換。
/// </summary>
public sealed class ScheduledRunsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);   // 台北 09:00

    private sealed class Rig
    {
        public InMemoryScheduledItemStore Items { get; } = new();

        public InMemoryScheduledRunStore Runs { get; } = new();

        public ManualTimeProvider Time { get; } = new(Now, HistoryTestSupport.Zone(8));

        public ReminderService Reminders { get; }

        public RecordingScheduledRunService Service { get; } = new();

        public RecordingScheduledRunViewer Viewer { get; } = new();

        public FakeSettingsWindowService Settings { get; } = new();

        public FakeConfirmService Confirm { get; } = new();

        public ScheduledRunsViewModel Vm { get; }

        public Rig()
        {
            Reminders = new ReminderService(Items, new Lumi.Core.Reminders.NullNotificationSink(), Time, new ReminderOptions(), NullLogger<ReminderService>.Instance);
            Vm = new ScheduledRunsViewModel(Runs, Reminders, Service, () => Viewer, Settings, Confirm, new SyncUiDispatcher(), Time,
                new ScheduledRunOptions());
        }
    }

    private static readonly RecurrenceTrigger Daily = new(RecurrenceKind.Daily, new TimeOnly(9, 0), null, null);

    private static ScheduledItem Prompt(string id, string title, Trigger? trigger = null, bool enabled = true,
        DateTimeOffset? next = null, ScheduledStatus status = ScheduledStatus.Pending) =>
        new(id, ScheduledKind.Schedule, title, "", trigger ?? Daily, "Asia/Taipei",
            new RunPromptAction("整理今天的信件", new ScheduleGrant(false, false, false), []),
            status, enabled, next ?? Now.AddDays(1), null, ScheduledCreator.User, Now.AddDays(-7));

    private static ScheduledRun Run(string id, string scheduleId, string title, DateTimeOffset planned,
        ScheduledRunStatus status = ScheduledRunStatus.Completed, ScheduledRunTrigger trigger = ScheduledRunTrigger.Scheduled,
        DateTimeOffset? readAt = null) =>
        new(id, scheduleId, title, "conv-" + id, planned, planned, planned.AddMinutes(1), trigger, status, null, readAt);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Yield();
        Assert.True(condition());
    }

    /// <summary>晨報（兩筆紀錄，新的在前）、週報（一筆）、還沒有紀錄的排程。</summary>
    private static async Task<Rig> SeedAsync()
    {
        var rig = new Rig();
        await rig.Items.AddAsync(Prompt("s-morning", "晨報"));
        await rig.Items.AddAsync(Prompt("s-weekly", "週報"));
        await rig.Items.AddAsync(Prompt("s-empty", "還沒跑過"));
        await rig.Runs.AddAsync(Run("r1", "s-morning", "晨報", Now.AddDays(-1)));
        await rig.Runs.AddAsync(Run("r2", "s-morning", "晨報", Now, readAt: Now));
        await rig.Runs.AddAsync(Run("r3", "s-weekly", "週報", Now.AddDays(-3)));
        await rig.Vm.ReloadAsync();
        return rig;
    }

    private static ScheduledGroupViewModel Group(Rig rig, string scheduleId) =>
        rig.Vm.Groups.Single(g => g.ScheduleId == scheduleId);

    // ---- 分組與排序 ----

    [Fact]
    public async Task Groups_AreOnePerSchedule_RunsNewestFirst_AndSchedulesWithoutRunsComeLast()
    {
        var rig = await SeedAsync();

        Assert.Equal(new[] { "晨報", "週報", "還沒跑過" }, rig.Vm.Groups.Select(g => g.Title).ToArray());
        Assert.Equal(new[] { "r2", "r1" }, Group(rig, "s-morning").Runs.Select(r => r.Id).ToArray());
        Assert.Empty(Group(rig, "s-empty").Runs);
        Assert.False(rig.Vm.IsEmpty);
    }

    [Fact]
    public async Task Subtitle_ShowsTheRuleAndTheNextTime_OrTheDisabledOrDeletedNote()
    {
        var rig = await SeedAsync();
        // 預設的下次時間是 11/1 01:00 UTC，台北（UTC+8）是 11/1 09:00
        Assert.Equal(string.Format(Strings.Schedule_GroupSubFormat, "每天 09:00", string.Format(Strings.Schedule_NextFormat, "11/1 09:00")),
            Group(rig, "s-morning").Subtitle);

        await rig.Reminders.SetEnabledAsync("s-morning", false, default);
        await WaitFor(() => Group(rig, "s-morning").Subtitle.EndsWith(Strings.Schedule_GroupDisabled));
        Assert.Equal($"每天 09:00 · {Strings.Schedule_GroupDisabled}", Group(rig, "s-morning").Subtitle);
    }

    [Fact]
    public async Task ScheduleDeletedButRunsKept_IsMarkedDeleted_AndOnlyCanDeleteItsRuns()
    {
        var rig = new Rig();
        await rig.Runs.AddAsync(Run("r9", "s-gone", "舊的晨報", Now.AddDays(-5)));
        await rig.Vm.ReloadAsync();

        var group = Group(rig, "s-gone");
        Assert.True(group.IsDeleted);
        Assert.Equal("舊的晨報", group.Title);
        Assert.Equal(Strings.Schedule_GroupDeleted, group.Subtitle);
        Assert.False(group.RunNowCommand.CanExecute(null));
        Assert.False(group.ToggleEnabledCommand.CanExecute(null));
        Assert.False(group.EditInSettingsCommand.CanExecute(null));
        Assert.True(group.DeleteRunsCommand.CanExecute(null));
    }

    [Fact]
    public async Task KeepNote_NamesTheKeptCount()
    {
        var rig = new Rig();
        Assert.Contains("30", rig.Vm.KeepNote);
    }

    [Fact]
    public async Task EmptyState_WhenThereAreNoSchedulesAndNoRuns()
    {
        var rig = new Rig();
        await rig.Vm.ReloadAsync();

        Assert.True(rig.Vm.IsEmpty);
        Assert.Empty(rig.Vm.Groups);
    }

    [Fact]
    public async Task InertViewModel_IsEmpty_AndReloadDoesNothing()
    {
        var vm = ScheduledRunsViewModel.CreateInert();

        await vm.ReloadAsync();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasUnread);
    }

    // ---- 未讀與徽章 ----

    [Fact]
    public async Task UnreadRuns_MarkTheTabAndTheRowDot()
    {
        var rig = await SeedAsync();

        Assert.True(rig.Vm.HasUnread);
        Assert.Equal(Strings.Schedule_TabRunsUnread, rig.Vm.TabName);
        Assert.Equal(1, Group(rig, "s-morning").Runs.Single(r => r.Id == "r1").DotOpacity);
        Assert.Equal(0, Group(rig, "s-morning").Runs.Single(r => r.Id == "r2").DotOpacity);

        await rig.Runs.MarkReadAsync("r1", Now);
        await rig.Runs.MarkReadAsync("r3", Now);
        await rig.Vm.ReloadAsync();

        Assert.False(rig.Vm.HasUnread);
        Assert.Equal(Strings.Schedule_TabRuns, rig.Vm.TabName);
    }

    [Fact]
    public async Task RunningRuns_AreNotUnreadYet()
    {
        var rig = new Rig();
        await rig.Runs.AddAsync(Run("r-run", "s-morning", "晨報", Now, ScheduledRunStatus.Running));
        await rig.Vm.ReloadAsync();

        Assert.False(rig.Vm.HasUnread);
    }

    [Theory]
    [InlineData(ScheduledRunStatus.Running, nameof(Strings.Schedule_Status_Running))]
    [InlineData(ScheduledRunStatus.AwaitingApproval, nameof(Strings.Schedule_Status_AwaitingApproval))]
    [InlineData(ScheduledRunStatus.Completed, nameof(Strings.Schedule_Status_Completed))]
    [InlineData(ScheduledRunStatus.Failed, nameof(Strings.Schedule_Status_Failed))]
    [InlineData(ScheduledRunStatus.Missed, nameof(Strings.Schedule_Status_Missed))]
    [InlineData(ScheduledRunStatus.Interrupted, nameof(Strings.Schedule_Status_Interrupted))]
    public void StatusBadge_UsesTheStatusString(ScheduledRunStatus status, string resourceName)
    {
        var rig = new Rig();
        var row = new ScheduledRunRowViewModel(rig.Vm, Run("r", "s", "晨報", Now, status), "09:00");

        Assert.Equal(typeof(Strings).GetProperty(resourceName)!.GetValue(null), row.StatusText);
    }

    [Fact]
    public void TriggerBadge_ShowsOnlyForCatchUpAndManual()
    {
        var rig = new Rig();
        Assert.Null(new ScheduledRunRowViewModel(rig.Vm, Run("a", "s", "t", Now), "").TriggerText);
        Assert.Equal(Strings.Schedule_Trigger_CatchUp,
            new ScheduledRunRowViewModel(rig.Vm, Run("b", "s", "t", Now, trigger: ScheduledRunTrigger.CatchUp), "").TriggerText);
        Assert.Equal(Strings.Schedule_Trigger_Manual,
            new ScheduledRunRowViewModel(rig.Vm, Run("c", "s", "t", Now, trigger: ScheduledRunTrigger.Manual), "").TriggerText);
    }

    // ---- 打開紀錄 ----

    [Fact]
    public async Task OpeningARun_CallsTheViewer_EvenForAMissedRunWithoutAConversation()
    {
        var rig = new Rig();
        var missed = Run("r-missed", "s-morning", "晨報", Now, ScheduledRunStatus.Missed);
        await rig.Runs.AddAsync(missed);
        await rig.Vm.ReloadAsync();

        var row = Group(rig, "s-morning").Runs.Single();
        await rig.Vm.OpenRunAsync(row);

        Assert.Equal(new[] { "r-missed" }, rig.Viewer.Opened);
    }

    [Fact]
    public async Task SelectingARunRow_OpensIt_ButReselectingTheSameRowDoesNot()
    {
        var rig = await SeedAsync();
        var group = Group(rig, "s-morning");
        var row = group.Runs[0];

        group.SelectedRun = row;
        group.SelectedRun = row;

        Assert.Equal(new[] { "r2" }, rig.Viewer.Opened);
    }

    // ---- 組的選單 ----

    [Fact]
    public async Task RunNow_CallsTheService_ShowsTheStartedLine_AndReloads()
    {
        var rig = await SeedAsync();
        var group = Group(rig, "s-morning");
        rig.Service.NextRunNow = Run("r-now", "s-morning", "晨報", Now, ScheduledRunStatus.Running, ScheduledRunTrigger.Manual);

        await group.RunNowCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "s-morning" }, rig.Service.RunNowCalls);
        Assert.Equal(string.Format(Strings.Schedule_RunNowStartedFormat, "晨報"), rig.Vm.Notice);
        Assert.True(rig.Vm.HasNotice);
    }

    [Fact]
    public async Task RunNow_WhenTheScheduleIsGone_ShowsNoLine()
    {
        var rig = await SeedAsync();
        rig.Service.NextRunNow = null;

        await Group(rig, "s-morning").RunNowCommand.ExecuteAsync(null);

        Assert.Null(rig.Vm.Notice);
    }

    [Fact]
    public async Task ToggleEnabled_DisablesThroughTheReminderService_ShowsTheLine_AndReloads()
    {
        var rig = await SeedAsync();
        var group = Group(rig, "s-morning");
        Assert.True(group.Enabled);

        await group.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.False((await rig.Items.GetAsync("s-morning"))!.Enabled);
        Assert.Equal(string.Format(Strings.Schedule_DisabledFormat, "晨報"), rig.Vm.Notice);
        var reloaded = Group(rig, "s-morning");
        Assert.False(reloaded.Enabled);
        Assert.Equal(Strings.Schedule_Enable, reloaded.ToggleText);
    }

    [Fact]
    public async Task ToggleEnabled_TurnsItBackOn()
    {
        var rig = await SeedAsync();
        await rig.Reminders.SetEnabledAsync("s-morning", false, default);
        await rig.Vm.ReloadAsync();

        await Group(rig, "s-morning").ToggleEnabledCommand.ExecuteAsync(null);

        Assert.True((await rig.Items.GetAsync("s-morning"))!.Enabled);
        Assert.Equal(string.Format(Strings.Schedule_EnabledFormat, "晨報"), rig.Vm.Notice);
    }

    [Fact]
    public async Task EditInSettings_OpensTheRemindersSectionWithThisSchedule()
    {
        var rig = await SeedAsync();

        await Group(rig, "s-morning").EditInSettingsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { new SettingsNavigation(SettingsSection.Reminders, ScheduleId: "s-morning") }, rig.Settings.Shown);
    }

    [Fact]
    public async Task ManageInSettings_OpensTheRemindersSection()
    {
        var rig = new Rig();

        await rig.Vm.ManageInSettingsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { new SettingsNavigation(SettingsSection.Reminders) }, rig.Settings.Shown);
    }

    [Fact]
    public async Task DeletedSchedule_DeleteRuns_AsksFirst_AndDeletesOnlyOnYes()
    {
        var rig = new Rig();
        await rig.Runs.AddAsync(Run("r9", "s-gone", "舊的晨報", Now.AddDays(-5)));
        await rig.Vm.ReloadAsync();
        var group = Group(rig, "s-gone");

        rig.Confirm.Answer = false;
        await group.DeleteRunsCommand.ExecuteAsync(null);
        Assert.Empty(rig.Service.DeletedSchedules);

        rig.Confirm.Answer = true;
        await group.DeleteRunsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "s-gone" }, rig.Service.DeletedSchedules);
        Assert.Contains("舊的晨報", rig.Confirm.Asked.Last().Message);
        Assert.Equal(Strings.Schedule_DeleteRuns, rig.Confirm.Asked.Last().ConfirmText);
    }

    // ---- 刪除一筆紀錄 ----

    [Fact]
    public async Task DeleteRun_AsksFirst_AndDeletesOnYes()
    {
        var rig = await SeedAsync();
        var row = Group(rig, "s-morning").Runs[0];
        Assert.True(row.DeleteCommand.CanExecute(null));

        rig.Confirm.Answer = false;
        await row.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(rig.Service.DeletedRuns);

        rig.Confirm.Answer = true;
        await row.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(new[] { row.Id }, rig.Service.DeletedRuns);
        Assert.Equal(Strings.Schedule_DeleteRunQuestion, rig.Confirm.Asked.Last().Message);
    }

    [Fact]
    public async Task ActiveRun_CannotBeDeleted_AndTheTipSaysWhy()
    {
        var rig = new Rig();
        await rig.Runs.AddAsync(Run("r-wait", "s-morning", "晨報", Now, ScheduledRunStatus.AwaitingApproval));
        await rig.Vm.ReloadAsync();
        var row = Group(rig, "s-morning").Runs.Single();

        Assert.False(row.DeleteCommand.CanExecute(null));
        Assert.Equal(Strings.Schedule_DeleteRunBlocked, row.DeleteTip);

        await rig.Vm.DeleteRunAsync(row);
        Assert.Empty(rig.Service.DeletedRuns);
        Assert.Empty(rig.Confirm.Asked);
    }

    // ---- 即時更新 ----

    [Fact]
    public async Task RunUpdated_ReloadsTheList()
    {
        var rig = await SeedAsync();
        var fresh = Run("r-new", "s-weekly", "週報", Now);
        await rig.Runs.AddAsync(fresh);

        rig.Service.Raise(fresh);

        await WaitFor(() => Group(rig, "s-weekly").Runs.Any(r => r.Id == "r-new"));
        Assert.Equal(new[] { "r-new", "r3" }, Group(rig, "s-weekly").Runs.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task ReminderChange_ReloadsTheSubtitle_WithoutAnyRunEvent()
    {
        var rig = await SeedAsync();
        Assert.True(Group(rig, "s-morning").Enabled);

        await rig.Reminders.SetEnabledAsync("s-morning", false, default);

        await WaitFor(() => !Group(rig, "s-morning").Enabled);
    }

    [Fact]
    public async Task Dispose_StopsListeningToRunUpdates()
    {
        var rig = await SeedAsync();
        rig.Vm.Dispose();
        var fresh = Run("r-late", "s-weekly", "週報", Now);
        await rig.Runs.AddAsync(fresh);

        rig.Service.Raise(fresh);

        Assert.DoesNotContain(Group(rig, "s-weekly").Runs, r => r.Id == "r-late");
    }

    // ---- 「對話｜排程」分頁切換（HistoryViewModel） ----

    [Fact]
    public async Task HistoryTabs_SwitchBetweenChatsAndRuns_KeepingTheSearchText()
    {
        var store = new StaticListStore();
        store.Add("c1", "今天的對話", new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
        var rig = await SeedAsync();
        var time = new ManualTimeProvider(Now);
        var host = new FakeHistoryHost();
        var history = new HistoryViewModel(store, host, time, null, null, null, null, null, rig.Vm);

        Assert.True(history.IsChatsTab);
        Assert.False(history.IsRunsTab);

        history.SearchText = "對話";
        history.IsRunsTab = true;

        Assert.False(history.IsChatsTab);
        Assert.Equal("對話", history.SearchText);
        Assert.Same(rig.Vm, history.Runs);
        await WaitFor(() => history.Runs.Groups.Count == 3);

        history.IsChatsTab = true;

        Assert.True(history.IsChatsTab);
        Assert.False(history.IsRunsTab);
        Assert.Equal("對話", history.SearchText);
    }

    [Fact]
    public async Task HistoryTabs_ChatsTabIsUnchanged_WithoutARunsViewModel()
    {
        var store = new StaticListStore();
        store.Add("c1", "今天的對話", new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
        var history = new HistoryViewModel(store, new FakeHistoryHost(), new ManualTimeProvider(Now));
        await history.RefreshAsync(null);

        Assert.True(history.IsChatsTab);
        Assert.Single(history.Items);
        Assert.False(history.Runs.HasUnread);
    }
}
