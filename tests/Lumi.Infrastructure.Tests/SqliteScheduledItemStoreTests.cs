using System.Globalization;
using System.Text.Json;
using Lumi.Core.Reminders;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class SqliteScheduledItemStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-sched-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewConversationStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    private SqliteScheduledItemStore NewStore() =>
        new(NewConversationStore(), NullLogger<SqliteScheduledItemStore>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

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

    /// <summary>直接用 SQL 寫一列（給「列壞掉」的情境用）。</summary>
    private void RawInsert(string id, string triggerJson)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scheduled_items (id, kind, title, message, trigger_json, time_zone_id, action_json, status, enabled, next_fire_at, last_fire_at, created_by, created_at)
            VALUES ($id, 0, '壞掉的', '內容', $trigger, 'Asia/Taipei', '{"type":"notify"}', 0, 1, '2026-10-23T01:00:00.0000000Z', NULL, 'Agent', '2026-10-20T00:00:00.0000000Z');
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$trigger", triggerJson);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task AddThenGet_RoundTripsEveryField()
    {
        var store = NewStore();
        var item = Item("a1b2c3d4", nextFireAt: "2026-10-23T09:00+08:00", lastFiredAt: "2026-10-22T09:00+08:00");

        await store.AddAsync(item);

        Assert.Equal(item, await store.GetAsync("a1b2c3d4"));
    }

    [Fact]
    public async Task GetMissing_ReturnsNull() =>
        Assert.Null(await NewStore().GetAsync("missing"));

    [Fact]
    public async Task Add_DuplicateId_Throws()
    {
        var store = NewStore();
        await store.AddAsync(Item("dup"));

        await Assert.ThrowsAsync<SqliteException>(() => store.AddAsync(Item("dup")));
    }

    [Fact]
    public async Task Update_ReplacesTheWholeRow_AndReturnsTrue()
    {
        var store = NewStore();
        await store.AddAsync(Item("u1"));
        var recurrence = Item("u1", status: ScheduledStatus.Pending) with
        {
            Trigger = new RecurrenceTrigger(RecurrenceKind.Weekly, new TimeOnly(9, 30), [DayOfWeek.Monday, DayOfWeek.Friday], null),
            NextFireAt = T("2026-10-26T09:30+08:00"),
            Enabled = false,
        };

        Assert.True(await store.UpdateAsync(recurrence));

        var read = await store.GetAsync("u1");
        // Trigger 的 Days 是 IReadOnlyList，record 相等比參考，所以拆開比（Trigger 比序列化後的內容）
        Assert.Equal(recurrence with { Trigger = null! }, read! with { Trigger = null! });
        Assert.Equal(JsonSerializer.Serialize(recurrence.Trigger), JsonSerializer.Serialize(read.Trigger));
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, ((RecurrenceTrigger)read.Trigger).Days);
    }

    [Fact]
    public async Task Update_MissingId_ReturnsFalse()
    {
        var store = NewStore();

        Assert.False(await store.UpdateAsync(Item("ghost")));
        Assert.Null(await store.GetAsync("ghost"));
    }

    [Fact]
    public async Task Times_AreStoredAsUtc_SoTheInstantSurvivesTheRoundTrip()
    {
        var store = NewStore();
        await store.AddAsync(Item("tz", nextFireAt: "2026-10-23T09:00+08:00"));

        var read = await store.GetAsync("tz");

        Assert.Equal(T("2026-10-23T09:00+08:00"), read!.NextFireAt);
        // 欄位內容一律是 UTC（字串排序才會等於時間排序）
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT next_fire_at FROM scheduled_items WHERE id = 'tz';";
        Assert.Equal("2026-10-23T01:00:00.0000000Z", command.ExecuteScalar());
    }

    [Fact]
    public async Task List_OrdersByNextFireAtOrCreatedAt_ThenId()
    {
        var store = NewStore();
        await store.AddAsync(Item("c", nextFireAt: "2026-10-24T09:00+08:00"));
        await store.AddAsync(Item("a", nextFireAt: null, createdAt: "2026-10-22T00:00+08:00"));
        await store.AddAsync(Item("b", nextFireAt: "2026-10-24T09:00+08:00"));

        var ids = (await store.ListAsync(new ScheduledItemFilter())).Select(item => item.Id).ToList();

        Assert.Equal(["a", "b", "c"], ids);
    }

    [Fact]
    public async Task List_OrdersAcrossDifferentOffsets_ByInstant()
    {
        var store = NewStore();
        // 字串上 +08:00 比較大，但 UTC 時間比較早；必須依真正的時間排
        await store.AddAsync(Item("later-utc", nextFireAt: "2026-10-23T10:00+00:00"));
        await store.AddAsync(Item("earlier-utc", nextFireAt: "2026-10-23T09:00+08:00"));

        var ids = (await store.ListAsync(new ScheduledItemFilter())).Select(item => item.Id).ToList();

        Assert.Equal(["earlier-utc", "later-utc"], ids);
    }

    [Fact]
    public async Task List_FilterByStatusesAndKinds()
    {
        var store = NewStore();
        await store.AddAsync(Item("p-reminder"));
        await store.AddAsync(Item("p-timer", kind: ScheduledKind.Timer));
        await store.AddAsync(Item("missed", status: ScheduledStatus.Missed));
        await store.AddAsync(Item("cancelled", status: ScheduledStatus.Cancelled));

        var pendingTimers = await store.ListAsync(new ScheduledItemFilter(
            Statuses: [ScheduledStatus.Pending], Kinds: [ScheduledKind.Timer]));
        var pendingOrMissed = await store.ListAsync(new ScheduledItemFilter(
            Statuses: [ScheduledStatus.Pending, ScheduledStatus.Missed]));

        Assert.Equal(["p-timer"], pendingTimers.Select(item => item.Id).ToList());
        Assert.Equal(["missed", "p-reminder", "p-timer"], pendingOrMissed.Select(item => item.Id).Order().ToList());
    }

    [Fact]
    public async Task List_EmptySet_MatchesNothing()
    {
        var store = NewStore();
        await store.AddAsync(Item("p"));

        Assert.Empty(await store.ListAsync(new ScheduledItemFilter(Statuses: [])));
        Assert.Empty(await store.ListAsync(new ScheduledItemFilter(Kinds: [])));
    }

    [Fact]
    public async Task List_Limit_TakesTheEarliestFirst()
    {
        var store = NewStore();
        await store.AddAsync(Item("late", nextFireAt: "2026-10-30T09:00+08:00"));
        await store.AddAsync(Item("early", nextFireAt: "2026-10-24T09:00+08:00"));
        await store.AddAsync(Item("mid", nextFireAt: "2026-10-26T09:00+08:00"));

        var list = await store.ListAsync(new ScheduledItemFilter(Limit: 2));

        Assert.Equal(["early", "mid"], list.Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task List_LimitBelowOne_Throws() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewStore().ListAsync(new ScheduledItemFilter(Limit: 0)));

    [Fact]
    public async Task CountPending_CountsDisabled_NotFinished()
    {
        var store = NewStore();
        await store.AddAsync(Item("on"));
        await store.AddAsync(Item("off", enabled: false));
        await store.AddAsync(Item("done", status: ScheduledStatus.Completed));
        await store.AddAsync(Item("gone", status: ScheduledStatus.Cancelled));

        Assert.Equal(2, await store.CountPendingAsync());
    }

    [Fact]
    public async Task GetDue_IsEnabledPending_AndNotLaterThanNow()
    {
        var store = NewStore();
        await store.AddAsync(Item("due-early", nextFireAt: "2026-10-23T08:00+08:00"));
        await store.AddAsync(Item("due-exact", nextFireAt: "2026-10-23T09:00+08:00"));
        await store.AddAsync(Item("future", nextFireAt: "2026-10-23T09:01+08:00"));
        await store.AddAsync(Item("disabled", enabled: false, nextFireAt: "2026-10-23T08:00+08:00"));
        await store.AddAsync(Item("finished", status: ScheduledStatus.Completed, nextFireAt: "2026-10-23T08:00+08:00"));

        var due = await store.GetDueAsync(T("2026-10-23T09:00+08:00"));

        Assert.Equal(["due-early", "due-exact"], due.Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task GetDue_ComparesByInstant_NotByText()
    {
        var store = NewStore();
        // 文字上 "2026-10-23T09:30+08:00" 比 "2026-10-23T10:00+00:00" 小，但實際是 01:30Z，早於 10:00Z 的 now
        await store.AddAsync(Item("taipei", nextFireAt: "2026-10-23T09:30+08:00"));

        var due = await store.GetDueAsync(T("2026-10-23T10:00+00:00"));

        Assert.Equal(["taipei"], due.Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task GetNextFireAt_IsTheEarliestEnabledPending()
    {
        var store = NewStore();
        await store.AddAsync(Item("disabled", enabled: false, nextFireAt: "2026-10-23T07:00+08:00"));
        await store.AddAsync(Item("finished", status: ScheduledStatus.Completed, nextFireAt: "2026-10-23T06:00+08:00"));
        await store.AddAsync(Item("later", nextFireAt: "2026-10-25T09:00+08:00"));
        await store.AddAsync(Item("first", nextFireAt: "2026-10-24T09:00+08:00"));

        Assert.Equal(T("2026-10-24T09:00+08:00"), await store.GetNextFireAtAsync());
    }

    [Fact]
    public async Task GetNextFireAt_NothingPending_ReturnsNull()
    {
        var store = NewStore();
        await store.AddAsync(Item("off", enabled: false));
        await store.AddAsync(Item("none", nextFireAt: null));

        Assert.Null(await store.GetNextFireAtAsync());
    }

    [Fact]
    public async Task GetNextFireAt_EmptyTable_ReturnsNull() =>
        Assert.Null(await NewStore().GetNextFireAtAsync());

    [Fact]
    public async Task Purge_RemovesOnlyFinishedItemsOlderThanTheCutoff()
    {
        var store = NewStore();
        var cutoff = T("2026-09-23T00:00+08:00");
        await store.AddAsync(Item("fired-old", status: ScheduledStatus.Completed, nextFireAt: "2026-10-01T09:00+08:00", lastFiredAt: "2026-09-01T09:00+08:00"));
        await store.AddAsync(Item("missed-old", status: ScheduledStatus.Missed, nextFireAt: "2026-09-10T09:00+08:00", lastFiredAt: null));
        await store.AddAsync(Item("fired-new", status: ScheduledStatus.Completed, nextFireAt: "2026-09-01T09:00+08:00", lastFiredAt: "2026-10-01T09:00+08:00", createdAt: "2026-01-01T00:00+08:00"));
        await store.AddAsync(Item("cancelled-old", status: ScheduledStatus.Cancelled, nextFireAt: null, createdAt: "2026-08-01T00:00+08:00"));
        await store.AddAsync(Item("pending-old", nextFireAt: "2026-01-01T09:00+08:00", createdAt: "2025-01-01T00:00+08:00"));

        var removed = await store.PurgeFinishedAsync(cutoff);

        Assert.Equal(3, removed);
        var left = (await store.ListAsync(new ScheduledItemFilter())).Select(item => item.Id).Order().ToList();
        Assert.Equal(["fired-new", "pending-old"], left);
    }

    [Fact]
    public async Task Purge_ComparesTimesByInstant()
    {
        var store = NewStore();
        // cutoff 是 2026-09-23 00:00Z。
        // edge 是 08:00+08:00 = 00:00Z，等於 cutoff，不早於 → 保留。
        // older 是 07:59+08:00 = 23:59Z 前一天，早於 cutoff → 刪。
        // 文字比較會把 "08:00+08:00" 與 "00:00Z" 排錯，這裡要的是時間點比較。
        await store.AddAsync(Item("edge", status: ScheduledStatus.Completed, nextFireAt: null, lastFiredAt: "2026-09-23T08:00+08:00"));
        await store.AddAsync(Item("older", status: ScheduledStatus.Completed, nextFireAt: null, lastFiredAt: "2026-09-23T07:59+08:00"));

        var removed = await store.PurgeFinishedAsync(T("2026-09-23T00:00Z"));

        Assert.Equal(1, removed);
        Assert.NotNull(await store.GetAsync("edge"));
        Assert.Null(await store.GetAsync("older"));
    }

    [Fact]
    public async Task UnreadableRow_IsSkippedAndLogged_NotFatalToTheList()
    {
        var store = NewStore();
        await store.AddAsync(Item("good"));
        RawInsert("bad", """{"type":"future_kind","At":"2026-10-23T09:00:00+08:00"}""");

        var list = await store.ListAsync(new ScheduledItemFilter());
        var due = await store.GetDueAsync(T("2026-10-24T00:00+08:00"));

        Assert.Equal(["good"], list.Select(item => item.Id).ToList());
        Assert.Equal(["good"], due.Select(item => item.Id).ToList());
        Assert.Null(await store.GetAsync("bad"));
        // 壞掉的列仍佔著上限（CountPending 不讀內容）
        Assert.Equal(2, await store.CountPendingAsync());
    }

    [Fact]
    public async Task Migration5_CreatesTheScheduledItemsTable_AndTheDueIndex()
    {
        await NewConversationStore().CreateAsync("觸發建立");

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('scheduled_items', 'ix_scheduled_items_due');";

        Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar()));
    }
}
