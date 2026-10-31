using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class SqliteScheduledRunStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-run-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewConversationStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    private SqliteScheduledRunStore NewStore() => new(NewConversationStore());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static DateTimeOffset T(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset Base = T("2026-10-20T09:00+08:00");

    /// <summary>計畫時間是 Base 往後 <paramref name="minutes"/> 分鐘；開始與結束時間只是讓紀錄看起來完整。</summary>
    private static ScheduledRun Run(
        string id,
        string scheduleId = "s1",
        string title = "每日摘要",
        int minutes = 0,
        ScheduledRunStatus status = ScheduledRunStatus.Completed,
        string? conversationId = null,
        DateTimeOffset? readAt = null) => new(
            id, scheduleId, title, conversationId,
            Base.AddMinutes(minutes),
            Base.AddMinutes(minutes + 1),
            status is ScheduledRunStatus.Running or ScheduledRunStatus.AwaitingApproval ? null : Base.AddMinutes(minutes + 2),
            ScheduledRunTrigger.Scheduled, status, null, readAt);

    private static async Task<string> NewRunConversationAsync(SqliteConversationStore conversations, string title = "排程")
    {
        var conversation = await conversations.CreateAsync(title, ConversationKind.ScheduledRun);
        return conversation.Id;
    }

    [Fact]
    public async Task AddThenGet_RoundTripsEveryField()
    {
        var conversations = NewConversationStore();
        var store = new SqliteScheduledRunStore(conversations);
        var conversationId = await NewRunConversationAsync(conversations);
        var run = new ScheduledRun(
            "run-1", "s1", "每日摘要", conversationId,
            T("2026-10-20T09:00+08:00"), T("2026-10-20T09:00:05+08:00"), T("2026-10-20T09:02+08:00"),
            ScheduledRunTrigger.CatchUp, ScheduledRunStatus.Failed, "模型沒有回應", T("2026-10-20T10:00+08:00"));

        await store.AddAsync(run);

        Assert.Equal(run, await store.GetAsync("run-1"));
    }

    [Fact]
    public async Task MissedRun_HasNoConversation_AndRoundTrips()
    {
        var store = NewStore();
        var missed = new ScheduledRun(
            "missed-1", "s1", "每日摘要", null,
            T("2026-10-20T09:00+08:00"), null, null,
            ScheduledRunTrigger.Scheduled, ScheduledRunStatus.Missed, null, null);

        await store.AddAsync(missed);

        Assert.Equal(missed, await store.GetAsync("missed-1"));
        Assert.Null(await store.GetByConversationAsync("anything"));
    }

    [Fact]
    public async Task GetMissing_ReturnsNull()
    {
        Assert.Null(await NewStore().GetAsync("missing"));
    }

    [Fact]
    public async Task GetByConversation_FindsRunOwningIt()
    {
        var conversations = NewConversationStore();
        var store = new SqliteScheduledRunStore(conversations);
        var conversationId = await NewRunConversationAsync(conversations);
        await store.AddAsync(Run("r1", conversationId: conversationId));

        Assert.Equal("r1", (await store.GetByConversationAsync(conversationId))!.Id);
    }

    [Fact]
    public async Task Update_ReplacesRow_AndReportsMissing()
    {
        var store = NewStore();
        await store.AddAsync(Run("r1", status: ScheduledRunStatus.Running));
        var finished = Run("r1", status: ScheduledRunStatus.Completed);

        Assert.True(await store.UpdateAsync(finished));
        Assert.Equal(finished, await store.GetAsync("r1"));
        Assert.False(await store.UpdateAsync(Run("missing")));
    }

    [Fact]
    public async Task ListGrouped_GroupsBySchedule_NewestFirstWithinGroup_AndLimitsEachGroup()
    {
        var store = NewStore();
        // 排程 s1 有 3 筆（分鐘 0、10、20），s2 有 1 筆（分鐘 30）；加入順序故意打亂
        await store.AddAsync(Run("s1-10", minutes: 10));
        await store.AddAsync(Run("s2-30", scheduleId: "s2", title: "週報", minutes: 30));
        await store.AddAsync(Run("s1-0", minutes: 0));
        await store.AddAsync(Run("s1-20", minutes: 20));

        var groups = await store.ListGroupedAsync(perScheduleLimit: 2);

        // 組依最新一筆由新到舊：s2（30）在前，s1（20）在後
        Assert.Equal(new[] { "s2", "s1" }, groups.Select(g => g.ScheduleId));
        Assert.Equal("週報", groups[0].ScheduleTitle);
        Assert.Equal(new[] { "s2-30" }, groups[0].Runs.Select(r => r.Id));
        // 組內由新到舊，每組最多 2 筆
        Assert.Equal(new[] { "s1-20", "s1-10" }, groups[1].Runs.Select(r => r.Id));
    }

    [Fact]
    public async Task ListGrouped_EmptyTable_ReturnsEmpty()
    {
        Assert.Empty(await NewStore().ListGroupedAsync(30));
    }

    [Fact]
    public async Task ListForSchedule_ReturnsOnlyThatSchedule_NewestFirst()
    {
        var store = NewStore();
        await store.AddAsync(Run("a", minutes: 0));
        await store.AddAsync(Run("b", minutes: 5));
        await store.AddAsync(Run("other", scheduleId: "s2", minutes: 9));

        var runs = await store.ListForScheduleAsync("s1");

        Assert.Equal(new[] { "b", "a" }, runs.Select(r => r.Id));
    }

    [Fact]
    public async Task CountUnread_ExcludesRunningAndReadRuns()
    {
        var store = NewStore();
        await store.AddAsync(Run("unread-done", minutes: 0));
        await store.AddAsync(Run("read-done", minutes: 1, readAt: Base));
        await store.AddAsync(Run("running", minutes: 2, status: ScheduledRunStatus.Running));
        await store.AddAsync(Run("waiting", minutes: 3, status: ScheduledRunStatus.AwaitingApproval));

        // 與 ScheduledRun.IsUnread 一致：執行中不算，待確認的算未讀
        Assert.Equal(2, await store.CountUnreadAsync());
    }

    [Fact]
    public async Task MarkRead_SetsReadAt_KeepsFirstReadTime()
    {
        var store = NewStore();
        await store.AddAsync(Run("r1"));
        Assert.Equal(1, await store.CountUnreadAsync());

        await store.MarkReadAsync("r1", T("2026-10-20T10:00+08:00"));
        await store.MarkReadAsync("r1", T("2026-10-21T10:00+08:00"));

        Assert.Equal(T("2026-10-20T10:00+08:00"), (await store.GetAsync("r1"))!.ReadAt);
        Assert.Equal(0, await store.CountUnreadAsync());
    }

    [Fact]
    public async Task ListBeyond_SkipsKeptRuns_AndActiveRuns()
    {
        var store = NewStore();
        // 0 最舊、4 最新；保留最新 2 筆（4、3），其餘 2、1、0 是超過上限的
        await store.AddAsync(Run("r0", minutes: 0, status: ScheduledRunStatus.AwaitingApproval));
        await store.AddAsync(Run("r1", minutes: 1, status: ScheduledRunStatus.Running));
        await store.AddAsync(Run("r2", minutes: 2));
        await store.AddAsync(Run("r3", minutes: 3));
        await store.AddAsync(Run("r4", minutes: 4));
        await store.AddAsync(Run("other", scheduleId: "s2", minutes: 0));

        var beyond = await store.ListBeyondAsync("s1", keep: 2);

        // 執行中與待確認的不列出
        Assert.Equal(new[] { "r2" }, beyond.Select(r => r.Id));
    }

    [Fact]
    public async Task ListBeyond_ReturnsOldestBeyondLimit_NewestFirst()
    {
        var store = NewStore();
        for (var i = 0; i < 5; i++)
            await store.AddAsync(Run($"r{i}", minutes: i));

        var beyond = await store.ListBeyondAsync("s1", keep: 3);

        Assert.Equal(new[] { "r1", "r0" }, beyond.Select(r => r.Id));
        Assert.Empty(await store.ListBeyondAsync("s1", keep: 5));
    }

    [Fact]
    public async Task MarkActiveInterrupted_ChangesOnlyActiveRuns()
    {
        var store = NewStore();
        await store.AddAsync(Run("running", status: ScheduledRunStatus.Running));
        await store.AddAsync(Run("waiting", minutes: 1, status: ScheduledRunStatus.AwaitingApproval));
        await store.AddAsync(Run("done", minutes: 2));
        var at = T("2026-10-20T12:00+08:00");

        var count = await store.MarkActiveInterruptedAsync(at);

        Assert.Equal(2, count);
        var running = await store.GetAsync("running");
        Assert.Equal(ScheduledRunStatus.Interrupted, running!.Status);
        Assert.Equal(at, running.FinishedAt);
        Assert.Equal(ScheduledRunStatus.Interrupted, (await store.GetAsync("waiting"))!.Status);
        Assert.Equal(ScheduledRunStatus.Completed, (await store.GetAsync("done"))!.Status);
    }

    [Fact]
    public async Task ListOrphanRunConversations_ReturnsScheduledConversationsWithoutRuns()
    {
        var conversations = NewConversationStore();
        var store = new SqliteScheduledRunStore(conversations);
        var orphan = await NewRunConversationAsync(conversations, "殘留");
        var owned = await NewRunConversationAsync(conversations, "有紀錄");
        await store.AddAsync(Run("r1", conversationId: owned));
        // 一般對話不論有沒有紀錄都不算
        await conversations.CreateAsync("一般");

        var orphans = await store.ListOrphanRunConversationsAsync();

        Assert.Equal(new[] { orphan }, orphans);
    }

    [Fact]
    public async Task Delete_RemovesRow_AndReportsMissing()
    {
        var store = NewStore();
        await store.AddAsync(Run("r1"));

        Assert.True(await store.DeleteAsync("r1"));
        Assert.Null(await store.GetAsync("r1"));
        Assert.False(await store.DeleteAsync("r1"));
    }

    [Fact]
    public async Task ReopenDatabase_KeepsRuns()
    {
        var conversations = NewConversationStore();
        var store = new SqliteScheduledRunStore(conversations);
        var conversationId = await NewRunConversationAsync(conversations);
        var run = Run("r1", conversationId: conversationId, readAt: Base.AddHours(1));
        await store.AddAsync(run);
        SqliteConnection.ClearAllPools();

        var reopened = new SqliteScheduledRunStore(NewConversationStore());

        Assert.Equal(run, await reopened.GetAsync("r1"));
    }
}
