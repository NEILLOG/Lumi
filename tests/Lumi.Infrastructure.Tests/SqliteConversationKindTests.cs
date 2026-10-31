using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M24：<c>conversations.kind</c>。排程執行的紀錄是 <see cref="ConversationKind.ScheduledRun"/>，一般列表、最近、搜尋都不看它。</summary>
public sealed class SqliteConversationKindTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-kind-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    private SqliteScheduledRunStore NewRunStore() => new(NewStore());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static ChatMessage User(string text) =>
        new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);

    private static ChatMessage Assistant(string text, TokenUsage? usage = null) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow, MessageStatus.Complete, "model-x", usage);

    [Fact]
    public async Task CreateWithoutKind_IsChat()
    {
        var store = NewStore();

        var created = await store.CreateAsync("一般");

        Assert.Equal(ConversationKind.Chat, created.Kind);
        Assert.Equal(ConversationKind.Chat, (await store.GetConversationAsync(created.Id))!.Kind);
    }

    [Fact]
    public async Task CreateScheduledRun_ReturnsAndReadsBackKind()
    {
        var store = NewStore();

        var created = await store.CreateAsync("排程", ConversationKind.ScheduledRun);

        Assert.Equal(ConversationKind.ScheduledRun, created.Kind);
        var found = await store.GetConversationAsync(created.Id);
        Assert.NotNull(found);
        Assert.Equal(ConversationKind.ScheduledRun, found.Kind);
        Assert.Equal("排程", found.Title);
    }

    [Fact]
    public async Task ScheduledRunConversation_NotReturnedByGetLatest()
    {
        var store = NewStore();
        var chat = await store.CreateAsync("一般");
        await store.AppendMessageAsync(chat.Id, User("一般的訊息"));
        await Task.Delay(5);
        var run = await store.CreateAsync("排程", ConversationKind.ScheduledRun);
        await store.AppendMessageAsync(run.Id, User("排程的訊息"));

        var latest = await store.GetLatestAsync();

        Assert.NotNull(latest);
        Assert.Equal(chat.Id, latest.Id);
        Assert.Equal(ConversationKind.Chat, latest.Kind);
    }

    [Fact]
    public async Task GetLatest_OnlyScheduledRuns_ReturnsNull()
    {
        var store = NewStore();
        var run = await store.CreateAsync("排程", ConversationKind.ScheduledRun);
        await store.AppendMessageAsync(run.Id, User("排程的訊息"));

        Assert.Null(await store.GetLatestAsync());
    }

    [Fact]
    public async Task ScheduledRunConversation_NotInList()
    {
        var store = NewStore();
        var chat = await store.CreateAsync("一般");
        await store.AppendMessageAsync(chat.Id, User("一般的訊息"));
        var run = await store.CreateAsync("排程", ConversationKind.ScheduledRun);
        await store.AppendMessageAsync(run.Id, User("排程的訊息"));

        var list = await store.ListAsync(10);

        Assert.Equal(new[] { chat.Id }, list.Select(c => c.Id));
        Assert.All(list, c => Assert.Equal(ConversationKind.Chat, c.Kind));
    }

    [Fact]
    public async Task ListPaging_SkipsScheduledRunsAcrossPages()
    {
        var store = NewStore();
        var chatIds = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var chat = await store.CreateAsync($"一般 {i}");
            await store.AppendMessageAsync(chat.Id, User($"訊息 {i}"));
            chatIds.Add(chat.Id);
            var run = await store.CreateAsync($"排程 {i}", ConversationKind.ScheduledRun);
            await store.AppendMessageAsync(run.Id, User($"排程訊息 {i}"));
        }

        var collected = new List<string>();
        ConversationCursor? cursor = null;
        while (true)
        {
            var page = await store.ListAsync(1, cursor);
            if (page.Count == 0)
                break;
            collected.AddRange(page.Select(c => c.Id));
            var last = page[^1];
            cursor = new ConversationCursor(last.UpdatedAt, last.Id);
        }

        Assert.Equal(chatIds.Order(), collected.Order());
    }

    [Fact]
    public async Task ScheduledRunConversation_NotInSearch_ButChatWithSameTextIs()
    {
        var store = NewStore();
        var chat = await store.CreateAsync("一般");
        await store.AppendMessageAsync(chat.Id, User("天氣預報的內容"));
        var run = await store.CreateAsync("排程", ConversationKind.ScheduledRun);
        await store.AppendMessageAsync(run.Id, User("天氣預報的內容"));
        var titled = await store.CreateAsync("天氣排程", ConversationKind.ScheduledRun);
        await store.AppendMessageAsync(titled.Id, User("沒有關鍵字"));

        var hits = await store.SearchAsync("天氣", 10);

        var hit = Assert.Single(hits);
        Assert.Equal(chat.Id, hit.Conversation.Id);
        Assert.Equal(ConversationKind.Chat, hit.Conversation.Kind);
    }

    [Fact]
    public async Task ScheduledRunConversation_TokensStillCountedInTotals()
    {
        var store = NewStore();
        var run = await store.CreateAsync("排程", ConversationKind.ScheduledRun);
        await store.AppendMessageAsync(run.Id, User("跑一次"));
        await store.AppendMessageAsync(run.Id, Assistant("回答", new TokenUsage(100, 20, 3, 4)));

        var totals = await store.GetTokenTotalsAsync(October);

        Assert.Equal(new TokenTotals(Input: 107, CacheRead: 4, Output: 20), totals);
    }

    [Fact]
    public async Task ScheduledRunConversation_ToolActionsStillListed()
    {
        var store = NewStore();
        var run = await store.CreateAsync("排程", ConversationKind.ScheduledRun);
        var toolMessage = new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
            [new ToolResultBlock("tu-1", "結果", false, "搜尋", "目標", "摘要", 12, ToolApproval.NotRequired)],
            DateTimeOffset.UtcNow);
        await store.AppendMessageAsync(run.Id, toolMessage);

        var actions = await store.ListRecentToolActionsAsync(10);

        Assert.Single(actions);
        Assert.Equal(run.Id, actions[0].ConversationId);
    }

    [Fact]
    public async Task DeleteConversation_CascadesScheduledRunRows()
    {
        var store = NewStore();
        var runs = NewRunStore();
        var run = await store.CreateAsync("排程", ConversationKind.ScheduledRun);
        await runs.AddAsync(new ScheduledRun(ScheduledRun.NewId(), "s1", "每日摘要", run.Id,
            October, October, October, ScheduledRunTrigger.Scheduled, ScheduledRunStatus.Completed, null, null));

        Assert.True(await store.DeleteAsync(run.Id));

        Assert.Empty(await runs.ListForScheduleAsync("s1"));
    }

    [Fact]
    public async Task ReopenDatabase_KeepsKindAndRuns()
    {
        var first = NewStore();
        var chat = await first.CreateAsync("一般");
        var run = await first.CreateAsync("排程", ConversationKind.ScheduledRun);
        var runId = ScheduledRun.NewId();
        await NewRunStore().AddAsync(new ScheduledRun(runId, "s1", "每日摘要", run.Id,
            October, null, null, ScheduledRunTrigger.Manual, ScheduledRunStatus.Running, null, null));
        SqliteConnection.ClearAllPools();

        var reopened = NewStore();
        var reopenedRuns = new SqliteScheduledRunStore(reopened);

        Assert.Equal(ConversationKind.Chat, (await reopened.GetConversationAsync(chat.Id))!.Kind);
        Assert.Equal(ConversationKind.ScheduledRun, (await reopened.GetConversationAsync(run.Id))!.Kind);
        Assert.Equal(runId, (await reopenedRuns.GetByConversationAsync(run.Id))!.Id);
    }
}
