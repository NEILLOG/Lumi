using System.Globalization;
using Lumi.Core.Reminders;

namespace Lumi.Core.Tests.Reminders;

/// <summary>記憶體版的存取規則。SQLite 版（Infrastructure 測試）跑同樣的情境。</summary>
public sealed class ScheduledItemStoreTests
{
    private static DateTimeOffset T(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static ScheduledItem Item(
        string id,
        ScheduledStatus status = ScheduledStatus.Pending,
        bool enabled = true,
        string? nextFireAt = "2026-10-23T09:00+08:00",
        string? lastFiredAt = null,
        ScheduledKind kind = ScheduledKind.Reminder,
        string createdAt = "2026-10-20T00:00+08:00") => new(
            Id: id,
            Kind: kind,
            Title: "標題",
            Message: "內容",
            Trigger: new OneTimeTrigger(T("2026-10-23T09:00+08:00")),
            TimeZoneId: "Asia/Taipei",
            Action: new NotifyAction(),
            Status: status,
            Enabled: enabled,
            NextFireAt: nextFireAt is null ? null : T(nextFireAt),
            LastFiredAt: lastFiredAt is null ? null : T(lastFiredAt),
            CreatedBy: ScheduledCreator.Agent,
            CreatedAt: T(createdAt));

    private static ScheduledItemFilter All() => new();

    [Fact]
    public async Task AddThenGet_ReturnsTheSameItem()
    {
        var store = new InMemoryScheduledItemStore();
        var item = Item("a1b2c3d4");

        await store.AddAsync(item);

        Assert.Equal(item, await store.GetAsync("a1b2c3d4"));
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task Add_DuplicateId_Throws()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("dup"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(Item("dup")));
    }

    [Fact]
    public async Task Update_ReplacesTheWholeItem_AndReturnsTrue()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("u1"));

        var updated = await store.UpdateAsync(Item("u1", status: ScheduledStatus.Cancelled));

        Assert.True(updated);
        Assert.Equal(ScheduledStatus.Cancelled, (await store.GetAsync("u1"))!.Status);
    }

    [Fact]
    public async Task Update_MissingId_ReturnsFalseAndDoesNotAdd()
    {
        var store = new InMemoryScheduledItemStore();

        Assert.False(await store.UpdateAsync(Item("ghost")));
        Assert.Null(await store.GetAsync("ghost"));
    }

    [Fact]
    public async Task List_OrdersByNextFireAtOrCreatedAt_ThenId()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("c", nextFireAt: "2026-10-24T09:00+08:00"));
        await store.AddAsync(Item("a", nextFireAt: null, createdAt: "2026-10-22T00:00+08:00"));   // 排序鍵是建立時間
        await store.AddAsync(Item("b", nextFireAt: "2026-10-24T09:00+08:00"));

        var ids = (await store.ListAsync(All())).Select(item => item.Id).ToList();

        Assert.Equal(["a", "b", "c"], ids);
    }

    [Fact]
    public async Task List_FilterByStatuses()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("p"));
        await store.AddAsync(Item("c", status: ScheduledStatus.Cancelled));
        await store.AddAsync(Item("m", status: ScheduledStatus.Missed));

        var list = await store.ListAsync(new ScheduledItemFilter(Statuses: [ScheduledStatus.Pending, ScheduledStatus.Missed]));

        Assert.Equal(["m", "p"], list.Select(item => item.Id).Order().ToList());
    }

    [Fact]
    public async Task List_FilterByKinds()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("r", kind: ScheduledKind.Reminder));
        await store.AddAsync(Item("t", kind: ScheduledKind.Timer));

        var list = await store.ListAsync(new ScheduledItemFilter(Kinds: [ScheduledKind.Timer]));

        Assert.Equal(["t"], list.Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task List_EmptyStatusSet_MatchesNothing()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("p"));

        Assert.Empty(await store.ListAsync(new ScheduledItemFilter(Statuses: [])));
    }

    [Fact]
    public async Task List_Limit_TakesTheEarliestFirst()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("late", nextFireAt: "2026-10-30T09:00+08:00"));
        await store.AddAsync(Item("early", nextFireAt: "2026-10-24T09:00+08:00"));
        await store.AddAsync(Item("mid", nextFireAt: "2026-10-26T09:00+08:00"));

        var list = await store.ListAsync(new ScheduledItemFilter(Limit: 2));

        Assert.Equal(["early", "mid"], list.Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task List_LimitBelowOne_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new InMemoryScheduledItemStore().ListAsync(new ScheduledItemFilter(Limit: 0)));

    [Fact]
    public async Task CountPending_CountsDisabledPendingItems_ButNotFinishedOnes()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("on"));
        await store.AddAsync(Item("off", enabled: false));
        await store.AddAsync(Item("done", status: ScheduledStatus.Completed));
        await store.AddAsync(Item("gone", status: ScheduledStatus.Cancelled));

        Assert.Equal(2, await store.CountPendingAsync());
    }

    [Fact]
    public async Task GetDue_IsEnabledPending_AndNotLaterThanNow()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("due-early", nextFireAt: "2026-10-23T08:00+08:00"));
        await store.AddAsync(Item("due-exact", nextFireAt: "2026-10-23T09:00+08:00"));
        await store.AddAsync(Item("future", nextFireAt: "2026-10-23T09:01+08:00"));
        await store.AddAsync(Item("disabled", enabled: false, nextFireAt: "2026-10-23T08:00+08:00"));
        await store.AddAsync(Item("finished", status: ScheduledStatus.Completed, nextFireAt: "2026-10-23T08:00+08:00"));

        var due = await store.GetDueAsync(T("2026-10-23T09:00+08:00"));

        Assert.Equal(["due-early", "due-exact"], due.Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task GetNextFireAt_IsTheEarliestEnabledPending()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("disabled", enabled: false, nextFireAt: "2026-10-23T07:00+08:00"));
        await store.AddAsync(Item("finished", status: ScheduledStatus.Completed, nextFireAt: "2026-10-23T06:00+08:00"));
        await store.AddAsync(Item("later", nextFireAt: "2026-10-25T09:00+08:00"));
        await store.AddAsync(Item("first", nextFireAt: "2026-10-24T09:00+08:00"));

        Assert.Equal(T("2026-10-24T09:00+08:00"), await store.GetNextFireAtAsync());
    }

    [Fact]
    public async Task GetNextFireAt_NothingPending_ReturnsNull()
    {
        var store = new InMemoryScheduledItemStore();
        await store.AddAsync(Item("off", enabled: false));

        Assert.Null(await store.GetNextFireAtAsync());
    }

    [Fact]
    public async Task Purge_RemovesFinishedItemsOlderThanTheCutoff_UsingLastFiredThenNextFireThenCreated()
    {
        var store = new InMemoryScheduledItemStore();
        var cutoff = T("2026-09-23T00:00+08:00");
        // 最後觸發時間很舊 → 刪
        await store.AddAsync(Item("fired-old", status: ScheduledStatus.Completed, nextFireAt: "2026-10-01T09:00+08:00", lastFiredAt: "2026-09-01T09:00+08:00"));
        // 沒有最後觸發時間，下次時間很舊 → 刪
        await store.AddAsync(Item("missed-old", status: ScheduledStatus.Missed, nextFireAt: "2026-09-10T09:00+08:00", lastFiredAt: null));
        // 最後觸發時間是新的，即使建立很久以前也不刪
        await store.AddAsync(Item("fired-new", status: ScheduledStatus.Completed, nextFireAt: "2026-09-01T09:00+08:00", lastFiredAt: "2026-10-01T09:00+08:00", createdAt: "2026-01-01T00:00+08:00"));
        // 取消的、沒有時間的，用建立時間判斷
        await store.AddAsync(Item("cancelled-old", status: ScheduledStatus.Cancelled, nextFireAt: null, createdAt: "2026-08-01T00:00+08:00"));
        // 還在待觸發的永遠不刪
        await store.AddAsync(Item("pending-old", nextFireAt: "2026-01-01T09:00+08:00", createdAt: "2025-01-01T00:00+08:00"));

        var removed = await store.PurgeFinishedAsync(cutoff);

        Assert.Equal(3, removed);
        var left = (await store.ListAsync(All())).Select(item => item.Id).Order().ToList();
        Assert.Equal(["fired-new", "pending-old"], left);
    }
}
