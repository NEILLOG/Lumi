using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Context;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>
/// M24：設定頁「提醒排程」的啟停開關、跑 prompt 的編輯與儲存、刪除的兩段式確認、設定視窗的 ScheduleId 導覽。
/// </summary>
public sealed class RemindersEditTests : IDisposable
{
    private readonly InMemoryScheduledItemStore _store = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture));
    private readonly ReminderService _service;
    private readonly InMemoryScheduledRunStore _runs = new();
    private readonly FakeScheduledRunService _runService = new();
    private readonly RemindersViewModel _vm;

    public RemindersEditTests()
    {
        _service = new ReminderService(_store, new Lumi.Core.Reminders.NullNotificationSink(), _time, new ReminderOptions(), NullLogger<ReminderService>.Instance);
        _vm = Create(new FakeConfirmService());
    }

    public void Dispose() => _vm.Dispose();

    private RemindersViewModel Create(IConfirmService confirm) =>
        new(_service, confirm, new SyncUiDispatcher(), _time, _runService, _runs, new ReminderOptions { MaxPromptChars = 20 });

    private NewReminder OneTime(string title) => new(title, "要喝水", new OneTimeTrigger(_time.GetUtcNow().AddHours(1)), "Asia/Taipei",
        ScheduledKind.Reminder, ScheduledCreator.User);

    private NewPromptSchedule Prompt(string name, string prompt, ScheduleGrant? grant = null, params ScheduleScopeItem[] scope) =>
        new(name, prompt, new OneTimeTrigger(_time.GetUtcNow().AddHours(1)), "Asia/Taipei", grant ?? ScheduleGrant.None, scope,
            ScheduledCreator.User);

    private ReminderRowViewModel Row(string title) => _vm.Pending.Concat(_vm.Finished).Single(r => r.Title == title);

    /// <summary>依序回答的確認（測試兩段式刪除用）；答完就回 false。</summary>
    private sealed class ScriptedConfirm(params bool[] answers) : IConfirmService
    {
        private readonly Queue<bool> _answers = new(answers);

        public List<string> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string message, string confirmText)
        {
            Asked.Add(message);
            return Task.FromResult(_answers.Count > 0 && _answers.Dequeue());
        }
    }

    [Fact]
    public async Task Badges_TellTheActionApart_ForNotifyAndRunPrompt()
    {
        await _service.CreateReminderAsync(OneTime("喝水"), default);
        await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理今天的行程"), default);

        await _vm.ReloadAsync();

        Assert.Equal(Strings.Reminders_KindNotify, Row("喝水").ActionText);
        Assert.False(Row("喝水").IsRunPrompt);
        Assert.Equal(Strings.Reminders_KindRunPrompt, Row("晨報").ActionText);
        Assert.True(Row("晨報").IsRunPrompt);
    }

    [Fact]
    public async Task PendingItems_HaveTheSwitch_AndTogglingDisablesThem()
    {
        var item = await _service.CreateReminderAsync(OneTime("喝水"), default);
        await _vm.ReloadAsync();
        Assert.True(Row("喝水").IsPending);
        Assert.True(Row("喝水").Enabled);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Reminders_EnabledSwitchTipFormat, "喝水"), Row("喝水").SwitchTip);

        await Row("喝水").ToggleEnabledCommand.ExecuteAsync(null);

        Assert.False(Row("喝水").Enabled);
        Assert.False((await _service.ListAsync(new ReminderQuery(null, null, 10), default)).Single(i => i.Id == item.Id).Enabled);
        Assert.Null(Row("喝水").StatusText);   // 已暫停改由開關表達，不再有狀態文字
        Assert.False(Row("喝水").HasErrorText);
    }

    [Fact]
    public async Task Switch_FailureShowsTheErrorOnTheRow_AndKeepsTheRealState()
    {
        var item = await _service.CreateReminderAsync(OneTime("喝水"), default);
        await _service.SetEnabledAsync(item.Id, false, default);   // 先停用，時間過了之後再啟用就會失敗
        _time.Advance(TimeSpan.FromHours(2));
        await _vm.ReloadAsync();

        await Row("喝水").ToggleEnabledCommand.ExecuteAsync(null);

        var row = Row("喝水");
        Assert.True(row.HasErrorText);
        Assert.Contains("已經過了", row.ErrorText);
        Assert.False(row.Enabled);
    }

    [Fact]
    public async Task OnlyRunPrompt_CanBeExpanded_AndExpansionShowsTheEditor()
    {
        await _service.CreateReminderAsync(OneTime("喝水"), default);
        await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理今天的行程"), default);
        await _vm.ReloadAsync();

        Assert.False(Row("喝水").CanEdit);
        Assert.True(Row("晨報").CanEdit);
        Row("晨報").ToggleEditCommand.Execute(null);

        Assert.True(Row("晨報").IsExpanded);
        Assert.Equal("整理今天的行程", Row("晨報").EditPrompt);
        Assert.True(Row("晨報").HasNoScope);
    }

    [Fact]
    public async Task Save_SendsThePromptAndTheGrant_AndReportsSaved()
    {
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "舊的"), default);
        await _vm.ReloadAsync();
        Row("晨報").ToggleEditCommand.Execute(null);

        Row("晨報").EditPrompt = "新的內容";
        Row("晨報").EditWrite = true;
        Row("晨報").EditDestructive = true;
        await Row("晨報").SaveCommand.ExecuteAsync(null);

        var stored = (await _service.ListAsync(new ReminderQuery(null, null, 10), default)).Single(i => i.Id == created.Id);
        var action = Assert.IsType<RunPromptAction>(stored.Action);
        Assert.Equal("新的內容", action.Prompt);
        Assert.True(action.Grant.Write);
        Assert.True(action.Grant.Destructive);
        Assert.False(action.Grant.Network);
        var saved = Row("晨報");
        Assert.Equal(Strings.Reminders_EditSaved, saved.EditMessage);
        Assert.False(saved.EditMessageIsError);
        Assert.True(saved.IsExpanded);   // 儲存後保持展開
    }

    [Fact]
    public async Task Save_ValidatesThePrompt_EmptyAndTooLong()
    {
        await _service.CreatePromptScheduleAsync(Prompt("晨報", "舊的"), default);
        await _vm.ReloadAsync();
        var row = Row("晨報");
        row.ToggleEditCommand.Execute(null);

        row.EditPrompt = "   ";
        Assert.Equal(Strings.Reminders_EditPromptEmpty, row.PromptError);
        Assert.True(row.HasPromptError);
        Assert.False(row.SaveCommand.CanExecute(null));

        row.EditPrompt = new string('字', 21);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Reminders_EditPromptTooLongFormat, 20), row.PromptError);
        Assert.False(row.SaveCommand.CanExecute(null));

        row.EditPrompt = new string('字', 20);
        Assert.Null(row.PromptError);
        Assert.True(row.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task RemovingAFolder_ThenSave_SendsTheRemainingScope()
    {
        var keep = new ScheduleScopeItem(ContextKind.Folder, "/Users/me/Notes", "筆記");
        var drop = new ScheduleScopeItem(ContextKind.File, "/Users/me/todo.md", "todo.md");
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理", null, keep, drop), default);
        await _vm.ReloadAsync();
        var row = Row("晨報");
        row.ToggleEditCommand.Execute(null);
        Assert.Equal(2, row.EditScope.Count);
        Assert.False(row.ScopeChanged);

        row.EditScope.Single(e => e.DisplayName == "todo.md").RemoveCommand.Execute(null);
        Assert.True(row.ScopeChanged);
        Assert.Single(row.EditScope);
        await row.SaveCommand.ExecuteAsync(null);

        var stored = (await _service.ListAsync(new ReminderQuery(null, null, 10), default)).Single(i => i.Id == created.Id);
        var scope = Assert.IsType<RunPromptAction>(stored.Action).Scope;
        Assert.Equal([keep], scope);
    }

    [Fact]
    public async Task SaveWhenTheItemIsGone_ReportsTheFailure()
    {
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理"), default);
        await _vm.ReloadAsync();
        var row = Row("晨報");
        row.ToggleEditCommand.Execute(null);
        await _service.DeleteAsync(created.Id, default);

        await row.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Reminders_EditSaveFailed, row.EditMessage);
        Assert.True(row.EditMessageIsError);
    }

    [Fact]
    public async Task DestructiveNote_SwitchesWhenTheBoxIsChecked()
    {
        await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理"), default);
        await _vm.ReloadAsync();
        var row = Row("晨報");

        Assert.Equal(Strings.Schedule_Grant_DestructiveNote, row.DestructiveNote);
        row.EditDestructive = true;
        Assert.Equal(Strings.Schedule_Grant_DestructiveOnNote, row.DestructiveNote);
    }

    [Fact]
    public async Task Delete_RunPromptWithoutRuns_AsksOnce_AndDeletes()
    {
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理"), default);
        await _vm.ReloadAsync();
        var confirm = new ScriptedConfirm(true);
        using var vm = Create(confirm);
        await vm.ReloadAsync();

        await vm.Pending.Single(r => r.Id == created.Id).DeleteCommand.ExecuteAsync(null);

        Assert.Equal([Strings.Reminders_DeleteRunPromptQuestion], confirm.Asked);
        Assert.Empty(_runService.DeletedSchedules);
        Assert.Empty(vm.Pending);
    }

    [Fact]
    public async Task Delete_WithRuns_AsksAgain_AndDeletesTheRunsWhenConfirmed()
    {
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理"), default);
        await _runs.AddAsync(RunFor(created.Id, "r1"), default);
        var confirm = new ScriptedConfirm(true, true);
        using var vm = Create(confirm);
        await vm.ReloadAsync();

        await vm.Pending.Single(r => r.Id == created.Id).DeleteCommand.ExecuteAsync(null);

        Assert.Equal(2, confirm.Asked.Count);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Reminders_DeleteWithRunsFormat, 1), confirm.Asked[1]);
        Assert.Equal([created.Id], _runService.DeletedSchedules);
        Assert.Empty(vm.Pending);
    }

    [Fact]
    public async Task Delete_WithRuns_KeepingThem_DeletesOnlyTheSchedule()
    {
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理"), default);
        await _runs.AddAsync(RunFor(created.Id, "r1"), default);
        using var vm = Create(new ScriptedConfirm(true, false));
        await vm.ReloadAsync();

        await vm.Pending.Single(r => r.Id == created.Id).DeleteCommand.ExecuteAsync(null);

        Assert.Empty(_runService.DeletedSchedules);
        Assert.Empty(vm.Pending);
    }

    [Fact]
    public async Task Delete_Declined_ChangesNothing()
    {
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理"), default);
        await _runs.AddAsync(RunFor(created.Id, "r1"), default);
        using var vm = Create(new ScriptedConfirm(false));
        await vm.ReloadAsync();

        await vm.Pending.Single(r => r.Id == created.Id).DeleteCommand.ExecuteAsync(null);

        Assert.Single(vm.Pending);
        Assert.Empty(_runService.DeletedSchedules);
    }

    [Fact]
    public async Task Reveal_SelectsAndExpandsTheRow_AndAsksForTheScroll()
    {
        var created = await _service.CreatePromptScheduleAsync(Prompt("晨報", "整理"), default);
        await _service.CreateReminderAsync(OneTime("喝水"), default);
        await _vm.ReloadAsync();
        string? revealed = null;
        _vm.RevealRequested += id => revealed = id;

        _vm.Reveal(created.Id);

        Assert.True(Row("晨報").IsSelected);
        Assert.True(Row("晨報").IsExpanded);
        Assert.False(Row("喝水").IsSelected);
        Assert.False(Row("喝水").IsExpanded);
        Assert.Equal(created.Id, revealed);
    }

    [Fact]
    public async Task Reveal_ANonRunPromptRow_SelectsButDoesNotExpand()
    {
        var reminder = await _service.CreateReminderAsync(OneTime("喝水"), default);
        await _vm.ReloadAsync();

        _vm.Reveal(reminder.Id);

        Assert.True(Row("喝水").IsSelected);
        Assert.False(Row("喝水").IsExpanded);
    }

    [Fact]
    public async Task Settings_ScheduleId_NavigatesToTheRemindersPage_AndRevealsTheRow()
    {
        using var host = new SettingsHost();
        var item = await host.ReminderService.CreatePromptScheduleAsync(
            new NewPromptSchedule("晨報", "整理", new OneTimeTrigger(DateTimeOffset.UtcNow.AddHours(1)), "Asia/Taipei", ScheduleGrant.None, [],
                ScheduledCreator.User), default);
        await host.Reminders.ReloadAsync();

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Reminders, ScheduleId: item.Id));

        Assert.True(host.Settings.ShowReminders);
        var row = host.Reminders.Pending.Single(r => r.Id == item.Id);
        Assert.True(row.IsSelected);
        Assert.True(row.IsExpanded);
    }

    private static ScheduledRun RunFor(string scheduleId, string runId) => new(
        runId, scheduleId, "晨報", null, DateTimeOffset.Parse("2026-10-23T08:00+08:00", CultureInfo.InvariantCulture), null, null,
        ScheduledRunTrigger.Scheduled, ScheduledRunStatus.Completed, null, null);
}
