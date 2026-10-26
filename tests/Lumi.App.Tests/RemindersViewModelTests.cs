using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M20 20B：設定頁「提醒排程」的 ViewModel——分組與排序、排程文字、刪除（確認）、清除已結束、變更訊號。</summary>
public sealed class RemindersViewModelTests : IDisposable
{
    private readonly InMemoryScheduledItemStore _store = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ReminderService _service;
    private readonly FakeConfirmService _confirm = new();
    private readonly RemindersViewModel _vm;

    public RemindersViewModelTests()
    {
        _service = new ReminderService(_store, new Lumi.Core.Reminders.NullNotificationSink(), _time, new ReminderOptions(), NullLogger<ReminderService>.Instance);
        _vm = new RemindersViewModel(_service, _confirm, new SyncUiDispatcher(), _time);
    }

    public void Dispose() => _vm.Dispose();

    private NewReminder OneTime(string title) => new(title, "要喝水", new OneTimeTrigger(_time.GetUtcNow().AddHours(1)), "Asia/Taipei",
        ScheduledKind.Reminder, ScheduledCreator.User);

    private static NewReminder Daily(string title) => new(title, "開會", new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(9, 0), null, null),
        "Asia/Taipei", ScheduledKind.Schedule, ScheduledCreator.Agent);

    private ReminderRowViewModel Row(string title) => _vm.Pending.Concat(_vm.Finished).Single(r => r.Title == title);

    [Fact]
    public async Task Reload_SplitsPendingAndFinished_AndDescribesRecurrence()
    {
        await _service.CreateReminderAsync(OneTime("喝水"), default);
        var daily = await _service.CreateReminderAsync(Daily("晨會"), default);

        await _vm.ReloadAsync();

        Assert.Equal(2, _vm.Pending.Count);
        Assert.Empty(_vm.Finished);
        Assert.True(_vm.ShowFinishedEmpty);
        Assert.False(_vm.ShowPendingEmpty);

        var dailyRow = _vm.Pending.Single(r => r.Id == daily.Id);
        Assert.Contains(ScheduleCalculator.Describe(daily.Trigger, daily.TimeZoneId), dailyRow.ScheduleText);
        Assert.Equal("每天 09:00 · 下次：2026/10/22 09:00", dailyRow.ScheduleText);
        Assert.Equal(Strings.Reminders_CreatedByAgent, dailyRow.CreatedByText);
        Assert.Null(dailyRow.StatusText);
    }

    [Fact]
    public async Task Cancel_MovesTheItemToFinished_WithTheCancelledStatus()
    {
        var item = await _service.CreateReminderAsync(OneTime("喝水"), default);
        Assert.Single(_vm.Pending);

        await _service.CancelAsync(item.Id, default);

        Assert.Empty(_vm.Pending);
        var row = Assert.Single(_vm.Finished);
        Assert.Equal(Strings.Reminders_StatusCancelled, row.StatusText);
        Assert.True(_vm.HasFinished);
    }

    [Fact]
    public async Task Delete_Confirmed_RemovesTheItem_AndReportsIt()
    {
        var item = await _service.CreateReminderAsync(OneTime("喝水"), default);
        _confirm.Answer = true;

        await Row("喝水").DeleteCommand.ExecuteAsync(null);

        Assert.Empty(_vm.Pending);
        Assert.Empty(await _service.ListAsync(new ReminderQuery(null, null, 100), default));
        Assert.Null((await _service.CancelAsync(item.Id, default)).Item);   // 已經不存在
        Assert.Equal(Strings.Reminders_Deleted, _vm.Message);
        Assert.False(_vm.MessageIsError);
        Assert.Contains(_confirm.Asked, a => a.ConfirmText == Strings.Reminders_Delete);
    }

    [Fact]
    public async Task Delete_NotConfirmed_KeepsTheItem_AndSaysNothing()
    {
        await _service.CreateReminderAsync(OneTime("喝水"), default);
        _confirm.Answer = false;

        await Row("喝水").DeleteCommand.ExecuteAsync(null);

        Assert.Single(_vm.Pending);
        Assert.Single(await _service.ListAsync(new ReminderQuery(null, null, 100), default));
        Assert.False(_vm.HasMessage);
    }

    [Fact]
    public async Task ClearFinished_RemovesAllFinishedItems_AndCountsThem()
    {
        var a = await _service.CreateReminderAsync(OneTime("甲"), default);
        var b = await _service.CreateReminderAsync(OneTime("乙"), default);
        await _service.CancelAsync(a.Id, default);
        await _service.CancelAsync(b.Id, default);
        Assert.Equal(2, _vm.Finished.Count);
        Assert.True(_vm.ClearFinishedCommand.CanExecute(null));

        await _vm.ClearFinishedCommand.ExecuteAsync(null);

        Assert.Empty(_vm.Finished);
        Assert.True(_vm.ShowFinishedEmpty);
        Assert.Equal(string.Format(Strings.Reminders_Cleared, 2), _vm.Message);
        Assert.Contains("2", _vm.Message);
    }

    [Fact]
    public void ClearFinished_IsDisabled_WhenNothingIsFinished()
    {
        Assert.False(_vm.ClearFinishedCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExternalCreate_AppearsInPending_ThroughTheChangeSignal()
    {
        Assert.Empty(_vm.Pending);

        await _service.CreateReminderAsync(OneTime("工具建立"), default);

        Assert.Single(_vm.Pending);
        Assert.Equal(Strings.Reminders_CreatedByUser, _vm.Pending[0].CreatedByText);
    }

    [Fact]
    public async Task AfterDispose_ChangesNoLongerReachTheList()
    {
        _vm.Dispose();

        await _service.CreateReminderAsync(OneTime("之後"), default);

        Assert.Empty(_vm.Pending);
    }
}
