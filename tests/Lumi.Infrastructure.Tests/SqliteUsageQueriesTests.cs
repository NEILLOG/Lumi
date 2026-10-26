using Lumi.Core.Chat;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class SqliteUsageQueriesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-usage-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static ChatMessage Assistant(DateTimeOffset at, TokenUsage? usage) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("回答")], at, MessageStatus.Complete, "model-x", usage);

    private static ChatMessage ToolMessage(DateTimeOffset at, params ToolResultBlock[] results) =>
        new(ChatMessage.NewId(), ChatRole.Tool, results, at);

    private static ToolResultBlock Result(string toolUseId, string name = "搜尋") =>
        new(toolUseId, "工具內容", false, name, "目標", "摘要", 12, ToolApproval.NotRequired);

    private async Task RawExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        // 直接改資料列，用來模擬 NULL 欄位與損毀的內容（正常寫入路徑不會產生）
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    // ---- GetTokenTotalsAsync ----

    [Fact]
    public async Task TokenTotals_IncludesCacheInInput_AndReportsCacheReadSeparately()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, Assistant(October.AddDays(1), new TokenUsage(100, 20, 3, 4)));

        var totals = await store.GetTokenTotalsAsync(October);

        Assert.Equal(new TokenTotals(Input: 107, CacheRead: 4, Output: 20), totals);
        Assert.Equal(127, totals.Total);
    }

    [Fact]
    public async Task TokenTotals_IncludesMessageExactlyAtSince_AndExcludesOneTickBefore()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, Assistant(October.AddTicks(-1), new TokenUsage(1000, 1000, 0, 0)));
        await store.AppendMessageAsync(conversation.Id, Assistant(October, new TokenUsage(7, 2, 0, 0)));

        var totals = await store.GetTokenTotalsAsync(October);

        Assert.Equal(new TokenTotals(Input: 7, CacheRead: 0, Output: 2), totals);
    }

    [Fact]
    public async Task TokenTotals_CrossMonthBoundary_OnlyCountsCurrentMonth()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        var septemberLastMinute = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);
        var octoberFirstMinute = new DateTimeOffset(2026, 10, 1, 0, 1, 0, TimeSpan.Zero);
        await store.AppendMessageAsync(conversation.Id, Assistant(septemberLastMinute, new TokenUsage(50, 50, 0, 0)));
        await store.AppendMessageAsync(conversation.Id, Assistant(octoberFirstMinute, new TokenUsage(5, 6, 0, 0)));

        var totals = await store.GetTokenTotalsAsync(October);

        Assert.Equal(new TokenTotals(Input: 5, CacheRead: 0, Output: 6), totals);
    }

    [Fact]
    public async Task TokenTotals_SinceInLocalOffset_IsConvertedToUtc()
    {
        // 台北時間 2026-10-01 00:00 = UTC 2026-09-30 16:00
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, Assistant(new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero), new TokenUsage(9, 1, 0, 0)));
        await store.AppendMessageAsync(conversation.Id, Assistant(new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero), new TokenUsage(100, 100, 0, 0)));
        var taipeiMonthStart = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8));

        var totals = await store.GetTokenTotalsAsync(taipeiMonthStart);

        Assert.Equal(new TokenTotals(Input: 9, CacheRead: 0, Output: 1), totals);
    }

    [Fact]
    public async Task TokenTotals_MessagesWithoutUsage_AreNotCounted()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, Assistant(October.AddHours(1), null));
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問")], October.AddHours(2)));
        await store.AppendMessageAsync(conversation.Id, Assistant(October.AddHours(3), new TokenUsage(4, 5, 0, 0)));

        var totals = await store.GetTokenTotalsAsync(October);

        Assert.Equal(new TokenTotals(Input: 4, CacheRead: 0, Output: 5), totals);
    }

    [Fact]
    public async Task TokenTotals_NullCacheColumns_CountAsZero()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, Assistant(October.AddHours(1), new TokenUsage(30, 10, 0, 0)));
        await RawExecuteAsync("UPDATE messages SET cache_creation_input_tokens = NULL, cache_read_input_tokens = NULL;");

        var totals = await store.GetTokenTotalsAsync(October);

        Assert.Equal(new TokenTotals(Input: 30, CacheRead: 0, Output: 10), totals);
    }

    [Fact]
    public async Task TokenTotals_SumsAcrossConversations()
    {
        var store = NewStore();
        var first = await store.CreateAsync("一");
        var second = await store.CreateAsync("二");
        await store.AppendMessageAsync(first.Id, Assistant(October.AddDays(2), new TokenUsage(10, 1, 0, 2)));
        await store.AppendMessageAsync(second.Id, Assistant(October.AddDays(3), new TokenUsage(20, 2, 0, 0)));

        var totals = await store.GetTokenTotalsAsync(October);

        Assert.Equal(new TokenTotals(Input: 32, CacheRead: 2, Output: 3), totals);
    }

    [Fact]
    public async Task TokenTotals_EmptyDatabase_IsZero()
    {
        Assert.Equal(TokenTotals.Zero, await NewStore().GetTokenTotalsAsync(October));
    }

    // ---- ListRecentToolActionsAsync ----

    [Fact]
    public async Task RecentToolActions_NewestFirst_AcrossConversations()
    {
        var store = NewStore();
        var first = await store.CreateAsync("一");
        var second = await store.CreateAsync("二");
        await store.AppendMessageAsync(first.Id, ToolMessage(October.AddMinutes(1), Result("t1", "舊")));
        await store.AppendMessageAsync(second.Id, ToolMessage(October.AddMinutes(3), Result("t2", "新")));
        await store.AppendMessageAsync(first.Id, ToolMessage(October.AddMinutes(2), Result("t3", "中")));

        var actions = await store.ListRecentToolActionsAsync(10);

        Assert.Equal(new[] {"t2", "t3", "t1"}, actions.Select(a => a.Result.ToolUseId));
        Assert.Equal(second.Id, actions[0].ConversationId);
        Assert.Equal(October.AddMinutes(3), actions[0].At);
        Assert.Equal("新", actions[0].Result.DisplayName);
    }

    [Fact]
    public async Task RecentToolActions_RespectsLimit()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        for (var i = 0; i < 5; i++)
            await store.AppendMessageAsync(conversation.Id, ToolMessage(October.AddMinutes(i), Result($"t{i}")));

        var actions = await store.ListRecentToolActionsAsync(2);

        Assert.Equal(new[] {"t4", "t3"}, actions.Select(a => a.Result.ToolUseId));
    }

    [Fact]
    public async Task RecentToolActions_OneMessageWithSeveralResults_ExpandsAndLimitCutsInside()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, ToolMessage(October.AddMinutes(1), Result("a"), Result("b"), Result("c")));
        await store.AppendMessageAsync(conversation.Id, ToolMessage(October.AddMinutes(2), Result("d")));

        var all = await store.ListRecentToolActionsAsync(10);
        var two = await store.ListRecentToolActionsAsync(2);

        // 同一則內，後面的區塊較晚執行，所以排在前面
        Assert.Equal(new[] {"d", "c", "b", "a"}, all.Select(a => a.Result.ToolUseId));
        Assert.Equal(new[] {"d", "c"}, two.Select(a => a.Result.ToolUseId));
        Assert.All(all.Where(a => a.Result.ToolUseId is "a" or "b" or "c"), a => Assert.Equal(October.AddMinutes(1), a.At));
    }

    [Fact]
    public async Task RecentToolActions_IgnoresNonToolMessages()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, Assistant(October.AddMinutes(1), new TokenUsage(1, 1, 0, 0)));
        await store.AppendMessageAsync(conversation.Id, ToolMessage(October.AddMinutes(2), Result("only")));

        var actions = await store.ListRecentToolActionsAsync(10);

        Assert.Equal(new[] {"only"}, actions.Select(a => a.Result.ToolUseId));
    }

    [Fact]
    public async Task RecentToolActions_CorruptedContentJson_IsSkipped()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        var broken = ToolMessage(October.AddMinutes(2), Result("broken"));
        await store.AppendMessageAsync(conversation.Id, ToolMessage(October.AddMinutes(1), Result("fine")));
        await store.AppendMessageAsync(conversation.Id, broken);
        await RawExecuteAsync("UPDATE messages SET content_json = '[{\"type\":' WHERE id = $id", ("$id", broken.Id));

        var actions = await store.ListRecentToolActionsAsync(10);

        Assert.Equal(new[] {"fine"}, actions.Select(a => a.Result.ToolUseId));
    }

    [Fact]
    public async Task RecentToolActions_ZeroLimit_ReturnsEmpty()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("對話");
        await store.AppendMessageAsync(conversation.Id, ToolMessage(October, Result("x")));

        Assert.Empty(await store.ListRecentToolActionsAsync(0));
    }

    [Fact]
    public async Task RecentToolActions_EmptyDatabase_ReturnsEmpty()
    {
        Assert.Empty(await NewStore().ListRecentToolActionsAsync(5));
    }
}
