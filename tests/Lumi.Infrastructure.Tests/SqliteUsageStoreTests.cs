using Lumi.Core.Chat;
using Lumi.Core.Usage;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M23 23B：<see cref="SqliteUsageStore"/>（request_usage 的存讀、區間、費用加總、刪除對話後費用保留）。</summary>
public sealed class SqliteUsageStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-usage-store-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset November = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private (SqliteConversationStore Conversations, SqliteUsageStore Usage) Open()
    {
        var conversations = new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));
        return (conversations, new SqliteUsageStore(conversations));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static UsageRecord Record(
        DateTimeOffset at, string? conversationId, decimal? cost, bool estimated = false,
        UsagePurpose purpose = UsagePurpose.Chat, ChatMode mode = ChatMode.Workbench) =>
        new(UsageRecord.NewId(), at, conversationId, purpose, mode, "setting-1", "model-x", "直連",
            new TokenUsage(100, 20, 3, 4), estimated,
            cost is null ? null : new PriceSnapshot(3m, 15m, 0.3m, 3.75m, IsFree: false), cost, 55);

    private static ChatMessage User(DateTimeOffset at) =>
        new(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問")], at);

    private static ChatMessage Assistant(DateTimeOffset at) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("答")], at, ModelId: "model-x", Usage: new TokenUsage(1, 1, 0, 0));

    [Fact]
    public async Task RoundTrip_KeepsEveryField()
    {
        var (conversations, usage) = Open();
        var conversation = await conversations.CreateAsync("對話");
        var record = Record(October.AddHours(5), conversation.Id, 0.4125m);
        var compaction = Record(October.AddHours(6), conversation.Id, 0.5m, estimated: true, UsagePurpose.Compaction, ChatMode.Sprite);

        await usage.AddAsync(record);
        await usage.AddAsync(compaction);

        var read = await usage.ListAsync(October, November);
        Assert.Equal([record, compaction], read);
    }

    [Fact]
    public async Task RoundTrip_NullConversationAndNullPriceAndCost()
    {
        var (_, usage) = Open();
        var record = Record(October.AddHours(1), null, null, estimated: true);

        await usage.AddAsync(record);

        var read = Assert.Single(await usage.ListAsync(October, November));
        Assert.Equal(record, read);
        Assert.Null(read.ConversationId);
        Assert.Null(read.Price);
        Assert.Null(read.CostUsd);
    }

    [Fact]
    public async Task ListAsync_RangeIsHalfOpen_AndOrderedByTime()
    {
        var (_, usage) = Open();
        var before = Record(October.AddTicks(-1), "c", 1m);
        var atStart = Record(October, "c", 1m);
        var threeDaysIn = Record(October.AddDays(3), "c", 1m);
        var lastTick = Record(November.AddTicks(-1), "c", 1m);
        var atEnd = Record(November, "c", 1m);
        foreach (var r in new[] { lastTick, atEnd, threeDaysIn, before, atStart })
            await usage.AddAsync(r);

        var read = await usage.ListAsync(October, November);

        Assert.Equal([atStart.Id, threeDaysIn.Id, lastTick.Id], read.Select(r => r.Id));
    }

    [Fact]
    public async Task SumCostAsync_SkipsNullCosts_AndRespectsRange()
    {
        var (_, usage) = Open();
        await usage.AddAsync(Record(October.AddDays(1), "c", 1.5m));
        await usage.AddAsync(Record(October.AddDays(2), "c", null));
        await usage.AddAsync(Record(October.AddDays(3), "c", 2.25m));
        await usage.AddAsync(Record(November.AddDays(1), "c", 100m));

        Assert.Equal(3.75m, await usage.SumCostAsync(October, November));
    }

    [Fact]
    public async Task SumCostAsync_EmptyIsZero()
    {
        var (_, usage) = Open();

        Assert.Equal(0m, await usage.SumCostAsync(October, November));
    }

    [Fact]
    public async Task GetConversationCost_SumsPriced_CountsUnpriced_AndEstimated()
    {
        var (conversations, usage) = Open();
        var conversation = await conversations.CreateAsync("對話");
        await usage.AddAsync(Record(October.AddHours(1), conversation.Id, 1m));
        await usage.AddAsync(Record(October.AddHours(2), conversation.Id, null));
        await usage.AddAsync(Record(October.AddHours(3), conversation.Id, 2m, estimated: true));

        var cost = await usage.GetConversationCostAsync(conversation.Id);

        Assert.Equal(new ConversationCost(3m, HasEstimated: true, UnpricedCount: 1), cost);
    }

    [Fact]
    public async Task GetConversationCost_AllUnpriced_IsNull_AndUnknownConversationIsEmpty()
    {
        var (conversations, usage) = Open();
        var conversation = await conversations.CreateAsync("對話");
        await usage.AddAsync(Record(October.AddHours(1), conversation.Id, null));

        Assert.Equal(new ConversationCost(null, HasEstimated: false, UnpricedCount: 1), await usage.GetConversationCostAsync(conversation.Id));
        Assert.Equal(ConversationCost.Empty, await usage.GetConversationCostAsync("沒有這則對話"));
    }

    [Fact]
    public async Task DeletedConversation_KeepsTheCost_AndIsNotListedAsTop()
    {
        var (conversations, usage) = Open();
        var conversation = await conversations.CreateAsync("要刪的對話");
        await conversations.AppendMessageAsync(conversation.Id, User(October.AddHours(1)));
        await usage.AddAsync(Record(October.AddHours(1), conversation.Id, 5m));

        Assert.True(await conversations.DeleteAsync(conversation.Id));

        Assert.Equal(5m, await usage.SumCostAsync(October, November));
        Assert.Equal(5m, (await usage.GetConversationCostAsync(conversation.Id)).CostUsd);
        Assert.Empty(await usage.ListTopConversationsAsync(October, November, 10));
    }

    [Fact]
    public async Task ListTopConversations_OrderedByCost_InRange_WithTurnsAndLastAt()
    {
        var (conversations, usage) = Open();
        var first = await conversations.CreateAsync("第一則");
        var second = await conversations.CreateAsync("第二則");
        var priced = await conversations.CreateAsync("沒有單價");
        var outside = await conversations.CreateAsync("上個月");

        await conversations.AppendMessageAsync(first.Id, User(October.AddDays(1)));
        await conversations.AppendMessageAsync(first.Id, Assistant(October.AddDays(1)));
        await conversations.AppendMessageAsync(first.Id, User(October.AddDays(4)));
        await conversations.AppendMessageAsync(second.Id, User(October.AddDays(2)));

        await usage.AddAsync(Record(October.AddDays(1), first.Id, 1m));
        await usage.AddAsync(Record(October.AddDays(5), first.Id, 0.5m, estimated: true));
        await usage.AddAsync(Record(October.AddDays(2), second.Id, 2m));
        await usage.AddAsync(Record(October.AddDays(4), priced.Id, null));
        await usage.AddAsync(Record(October.AddDays(-1), outside.Id, 9m));
        await usage.AddAsync(Record(October.AddDays(3), null, 100m));   // 沒有對話的紀錄不列

        var top = await usage.ListTopConversationsAsync(October, November, 10);

        Assert.Equal(
            [
                new ConversationCostItem(second.Id, "第二則", October.AddDays(2), TurnCount: 1, CostUsd: 2m, HasEstimated: false),
                new ConversationCostItem(first.Id, "第一則", October.AddDays(5), TurnCount: 2, CostUsd: 1.5m, HasEstimated: true),
            ],
            top);
        Assert.Single(await usage.ListTopConversationsAsync(October, November, 1));
    }
}
