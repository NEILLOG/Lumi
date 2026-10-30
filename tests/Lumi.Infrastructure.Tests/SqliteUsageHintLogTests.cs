using Lumi.Core.Usage;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M23 23I：<see cref="SqliteUsageHintLog"/>（usage_hints 的存讀、改結果、Shown 判斷、最近一次、區間查詢）。</summary>
public sealed class SqliteUsageHintLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-usage-hint-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset November = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private (SqliteConversationStore Conversations, SqliteUsageHintLog Hints) Open()
    {
        var conversations = new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));
        return (conversations, new SqliteUsageHintLog(conversations));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static UsageHintEntry Entry(
        DateTimeOffset at, string rule = "long-conversation", string? conversationId = "c1",
        UsageHintOutcome outcome = UsageHintOutcome.Shown) =>
        new(Guid.CreateVersion7().ToString(), rule, conversationId, at, outcome);

    [Fact]
    public async Task RoundTrip_KeepsEveryField_AndEveryOutcome()
    {
        var (_, hints) = Open();
        var outcomes = Enum.GetValues<UsageHintOutcome>();
        var entries = outcomes.Select((outcome, i) => Entry(October.AddHours(i + 1), outcome: outcome)).ToList();
        foreach (var entry in entries)
            await hints.AddAsync(entry);

        var read = await hints.ListAsync(October, November);

        Assert.Equal(entries, read);
    }

    [Fact]
    public async Task RoundTrip_NullConversation()
    {
        var (_, hints) = Open();
        var entry = Entry(October.AddHours(1), conversationId: null);

        await hints.AddAsync(entry);

        Assert.Equal([entry], await hints.ListAsync(October, November));
    }

    [Fact]
    public async Task SetOutcome_ChangesTheSameRow()
    {
        var (_, hints) = Open();
        var entry = Entry(October.AddHours(1));
        await hints.AddAsync(entry);

        await hints.SetOutcomeAsync(entry.Id, UsageHintOutcome.Dismissed);

        var read = Assert.Single(await hints.ListAsync(October, November));
        Assert.Equal(entry with { Outcome = UsageHintOutcome.Dismissed }, read);
    }

    [Fact]
    public async Task ListAsync_RangeIsHalfOpen_AndOrderedByTime()
    {
        var (_, hints) = Open();
        var before = Entry(October.AddTicks(-1));
        var atStart = Entry(October);
        var threeDaysIn = Entry(October.AddDays(3));
        var lastTick = Entry(November.AddTicks(-1));
        var atEnd = Entry(November);
        foreach (var e in new[] { lastTick, atEnd, threeDaysIn, before, atStart })
            await hints.AddAsync(e);

        var read = await hints.ListAsync(October, November);

        Assert.Equal([atStart.Id, threeDaysIn.Id, lastTick.Id], read.Select(e => e.Id));
    }

    [Fact]
    public async Task WasShownAsync_FalseWithoutRecords()
    {
        var (_, hints) = Open();

        Assert.False(await hints.WasShownAsync("long-conversation", "c1"));
    }

    [Fact]
    public async Task WasShownAsync_TrueAfterShown_AndStillTrueAfterAnyVisibleOutcome()
    {
        var (_, hints) = Open();
        var entry = Entry(October.AddHours(1));
        await hints.AddAsync(entry);
        Assert.True(await hints.WasShownAsync("long-conversation", "c1"));

        foreach (var outcome in new[] { UsageHintOutcome.Dismissed, UsageHintOutcome.Muted, UsageHintOutcome.Acted })
        {
            await hints.SetOutcomeAsync(entry.Id, outcome);
            Assert.True(await hints.WasShownAsync("long-conversation", "c1"));
        }
    }

    [Fact]
    public async Task WasShownAsync_SuppressedDoesNotCount()
    {
        var (_, hints) = Open();
        await hints.AddAsync(Entry(October.AddHours(1), outcome: UsageHintOutcome.Suppressed));

        Assert.False(await hints.WasShownAsync("long-conversation", "c1"));
    }

    [Fact]
    public async Task WasShownAsync_MatchesOnlyTheSameRuleAndConversation()
    {
        var (_, hints) = Open();
        await hints.AddAsync(Entry(October.AddHours(1), rule: "big-file", conversationId: "c1"));
        await hints.AddAsync(Entry(October.AddHours(2), rule: "long-conversation", conversationId: "c2"));

        Assert.False(await hints.WasShownAsync("long-conversation", "c1"));
        Assert.False(await hints.WasShownAsync("big-file", "c2"));
        Assert.True(await hints.WasShownAsync("big-file", "c1"));
    }

    [Fact]
    public async Task LastShownAsync_NullWithoutRecords()
    {
        var (_, hints) = Open();

        Assert.Null(await hints.LastShownAsync("mcp-tools"));
    }

    [Fact]
    public async Task LastShownAsync_IsTheLatestVisibleRecordOfThatRule()
    {
        var (_, hints) = Open();
        await hints.AddAsync(Entry(October.AddDays(1), rule: "mcp-tools", conversationId: "c1"));
        await hints.AddAsync(Entry(October.AddDays(5), rule: "mcp-tools", conversationId: "c2", outcome: UsageHintOutcome.Dismissed));
        await hints.AddAsync(Entry(October.AddDays(9), rule: "mcp-tools", conversationId: "c3", outcome: UsageHintOutcome.Suppressed));
        await hints.AddAsync(Entry(October.AddDays(12), rule: "big-file", conversationId: "c4"));

        Assert.Equal(October.AddDays(5), await hints.LastShownAsync("mcp-tools"));
    }

    [Fact]
    public async Task LastShownAsync_OnlySuppressedIsNull()
    {
        var (_, hints) = Open();
        await hints.AddAsync(Entry(October.AddDays(1), rule: "mcp-tools", outcome: UsageHintOutcome.Suppressed));

        Assert.Null(await hints.LastShownAsync("mcp-tools"));
    }

    [Fact]
    public async Task Records_SurviveReopeningTheDatabase()
    {
        var (_, hints) = Open();
        await hints.AddAsync(Entry(October.AddHours(1), conversationId: "c1"));
        SqliteConnection.ClearAllPools();

        var (_, reopened) = Open();

        Assert.True(await reopened.WasShownAsync("long-conversation", "c1"));
    }

    [Fact]
    public async Task DeletedConversation_KeepsTheRecord()
    {
        var (conversations, hints) = Open();
        var conversation = await conversations.CreateAsync("要刪的對話");
        await hints.AddAsync(Entry(October.AddHours(1), conversationId: conversation.Id));

        await conversations.DeleteAsync(conversation.Id);

        Assert.True(await hints.WasShownAsync("long-conversation", conversation.Id));
    }
}
