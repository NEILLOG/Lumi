using Lumi.Core.Chat;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M22 22H：上下文壓縮紀錄（<c>conversation_compactions</c>）的存取、順序號、用量計入與 cascade。</summary>
public sealed class SqliteCompactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-compaction-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "lumi.db");

    private SqliteConversationStore NewStore() => new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage Message(ChatRole role, string text, TokenUsage? usage = null) =>
        new(ChatMessage.NewId(), role, [new TextBlock(text)], DateTimeOffset.UtcNow, Usage: usage);

    private static CompactionRecord Record(CompactionKind kind, int upto, string? summary = null, TokenUsage? usage = null, DateTimeOffset? at = null) =>
        new(CompactionRecord.NewId(), kind, upto, summary, "model", at ?? DateTimeOffset.UtcNow, usage);

    [Fact]
    public async Task GetMessages_FillsSeq_StartingAtOne()
    {
        var store = NewStore();
        var c = await store.CreateAsync("c");
        await store.AppendMessageAsync(c.Id, Message(ChatRole.User, "a"));
        await store.AppendMessageAsync(c.Id, Message(ChatRole.Assistant, "b"));

        Assert.Equal([1, 2], (await store.GetMessagesAsync(c.Id)).Select(m => m.Seq));
    }

    [Fact]
    public async Task NoCompactions_StateIsEmpty_AndUsageIsZero()
    {
        var store = NewStore();
        var c = await store.CreateAsync("c");

        Assert.Equal(CompactionState.Empty, await store.GetCompactionStateAsync(c.Id));
        Assert.Equal(TokenUsage.Zero, await store.GetCompactionUsageAsync(c.Id));
    }

    [Fact]
    public async Task State_TakesTheLatestOfEachKind_AndRemembersTheLastMessageSeq()
    {
        var store = NewStore();
        var c = await store.CreateAsync("c");
        for (var i = 0; i < 6; i++)
            await store.AppendMessageAsync(c.Id, Message(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i}"));
        var t0 = DateTimeOffset.UtcNow;
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.ToolResults, 2, at: t0));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.Summary, 2, "第一份", at: t0.AddSeconds(1)));
        await store.AppendMessageAsync(c.Id, Message(ChatRole.User, "m6"));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.ToolResults, 4, at: t0.AddSeconds(2)));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.Summary, 4, "第二份", at: t0.AddSeconds(3)));

        var state = await NewStore().GetCompactionStateAsync(c.Id);   // 重開之後仍在

        Assert.Equal(4, state.ToolResultsUptoSeq);
        Assert.Equal(4, state.SummaryUptoSeq);
        Assert.Equal("第二份", state.Summary);
        Assert.Equal(7, state.AppliedAtSeq);
    }

    [Fact]
    public async Task OnlyToolResultsRow_LeavesSummaryEmpty()
    {
        var store = NewStore();
        var c = await store.CreateAsync("c");
        await store.AppendMessageAsync(c.Id, Message(ChatRole.User, "a"));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.ToolResults, 1));

        var state = await store.GetCompactionStateAsync(c.Id);

        Assert.Equal(1, state.ToolResultsUptoSeq);
        Assert.Null(state.SummaryUptoSeq);
        Assert.Null(state.Summary);
    }

    [Fact]
    public async Task CompactionUsage_IsSummedPerConversation_AndCountsInTheMonthlyTotals()
    {
        var store = NewStore();
        var c = await store.CreateAsync("c");
        var other = await store.CreateAsync("other");
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.AppendMessageAsync(c.Id, Message(ChatRole.Assistant, "答", new TokenUsage(100, 20, 3, 4)));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.Summary, 1, "s", new TokenUsage(1000, 200, 30, 40)));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.Summary, 2, "s2", new TokenUsage(10, 2, 0, 1)));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.ToolResults, 1));   // 第一層沒有用量
        await store.AddCompactionAsync(other.Id, Record(CompactionKind.Summary, 1, "s", new TokenUsage(5, 5, 0, 0)));

        Assert.Equal(new TokenUsage(1010, 202, 30, 41), await store.GetCompactionUsageAsync(c.Id));
        var totals = await store.GetTokenTotalsAsync(since);
        Assert.Equal(100 + 3 + 4 + 1000 + 30 + 40 + 10 + 1 + 5, totals.Input);
        Assert.Equal(4 + 40 + 1, totals.CacheRead);
        Assert.Equal(20 + 200 + 2 + 5, totals.Output);
        Assert.Equal(TokenTotals.Zero, await store.GetTokenTotalsAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task DeletingTheConversation_CascadesToItsCompactions()
    {
        var store = NewStore();
        var c = await store.CreateAsync("c");
        await store.AppendMessageAsync(c.Id, Message(ChatRole.User, "a"));
        await store.AddCompactionAsync(c.Id, Record(CompactionKind.Summary, 1, "s"));

        await store.DeleteAsync(c.Id);

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM conversation_compactions;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task UpgradeFromVersion10_AddsTheCompactionTable_AndKeepsConversations()
    {
        var store = NewStore();
        var c = await store.CreateAsync("M22 之前的資料");
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE conversation_compactions; DROP TABLE IF EXISTS request_usage; DROP TABLE IF EXISTS usage_hints; UPDATE schema_version SET version = 10;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopened = NewStore();

        Assert.Equal(c.Id, (await reopened.GetLatestAsync())?.Id);
        Assert.Equal(CompactionState.Empty, await reopened.GetCompactionStateAsync(c.Id));
        using var check = new SqliteConnection($"Data Source={DatabasePath}");
        check.Open();
        Assert.Equal(SqliteSchema.LatestVersion, SqliteSchema.ReadVersion(check));
    }
}
