using Lumi.Core.Reminders;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Reminders;

/// <summary>設定頁的真正刪除：刪除後不再響、變更訊號的取消規則。</summary>
public sealed class ReminderServiceDeleteTests
{
    [Fact]
    public async Task Delete_PendingItem_ThenProcessDue_DoesNotNotify()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        await h.Service.DeleteAsync(item.Id, default);
        h.Clock.SetNow(ReminderTestData.T("2026-10-23T09:00+08:00"));

        await h.Service.ProcessDueAsync(default);

        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public async Task Delete_ExistingItem_ReturnsTrue()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);

        Assert.True(await h.Service.DeleteAsync(item.Id, default));
    }

    [Fact]
    public async Task Delete_ExistingItem_RemovesItFromTheStore()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);

        await h.Service.DeleteAsync(item.Id, default);

        Assert.Null(await h.Store.GetAsync(item.Id));
    }

    [Fact]
    public async Task Delete_ExistingItem_CancelsTheChangeToken()
    {
        var h = new ReminderHarness();
        var item = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        var token = h.Service.ChangeToken;

        await h.Service.DeleteAsync(item.Id, default);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Delete_MissingId_ReturnsFalse()
    {
        var h = new ReminderHarness();

        Assert.False(await h.Service.DeleteAsync("ghost", default));
    }

    [Fact]
    public async Task Delete_MissingId_DoesNotCancelTheChangeToken()
    {
        var h = new ReminderHarness();
        var token = h.Service.ChangeToken;

        await h.Service.DeleteAsync("ghost", default);

        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteFinished_RemovesFinishedItems_AndReturnsTheCount()
    {
        var h = new ReminderHarness();
        var done = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "甲"), default);
        await h.Service.CancelAsync(done.Id, default);

        var removed = await h.Service.DeleteFinishedAsync(default);

        Assert.Equal(1, removed);
        Assert.Null(await h.Store.GetAsync(done.Id));
    }

    [Fact]
    public async Task DeleteFinished_KeepsPendingItems()
    {
        var h = new ReminderHarness();
        var done = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00", title: "甲"), default);
        var pending = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T10:00+08:00", title: "乙"), default);
        await h.Service.CancelAsync(done.Id, default);

        await h.Service.DeleteFinishedAsync(default);

        Assert.NotNull(await h.Store.GetAsync(pending.Id));
    }

    [Fact]
    public async Task DeleteFinished_RemovedSomething_CancelsTheChangeToken()
    {
        var h = new ReminderHarness();
        var done = await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        await h.Service.CancelAsync(done.Id, default);
        var token = h.Service.ChangeToken;

        await h.Service.DeleteFinishedAsync(default);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteFinished_NothingRemoved_DoesNotCancelTheChangeToken()
    {
        var h = new ReminderHarness();
        await h.Service.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);
        var token = h.Service.ChangeToken;

        var removed = await h.Service.DeleteFinishedAsync(default);

        Assert.Equal(0, removed);
        Assert.False(token.IsCancellationRequested);
    }
}
