using Lumi.Core.Chat;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class SearchTextBackfillTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-backfill-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    private SearchTextBackfill NewBackfill(SqliteConversationStore store) =>
        new(store, NullLogger<SearchTextBackfill>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage Message(ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, DateTimeOffset.UtcNow);

    /// <summary>模擬 migration 4 之前的資料：搜尋文字是空的，而且補寫旗標不存在。</summary>
    private void SimulateBeforeBackfill()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET search_text = ''; DELETE FROM app_meta;";
        command.ExecuteNonQuery();
    }

    private string? SearchTextOf(string messageId)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT search_text FROM messages WHERE id = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        return (string?)command.ExecuteScalar();
    }

    [Fact]
    public async Task Backfill_FillsOldMessages_ThenDoesNothingOnTheNextRun()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("舊對話");
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("舊的文字內容")));
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.Assistant, new TextBlock("助理的回答")));
        SimulateBeforeBackfill();
        Assert.Empty(await store.SearchAsync("舊的文字", 10));

        var first = await NewBackfill(store).RunAsync();
        var second = await NewBackfill(store).RunAsync();

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Single(await store.SearchAsync("舊的文字", 10));
        Assert.Single(await store.SearchAsync("助理的回答", 10));
    }

    [Fact]
    public async Task Backfill_CrossesBatchBoundaries()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("很長的對話");
        const int count = SearchTextBackfill.BatchSize * 2 + 7;
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var message = Message(ChatRole.User, new TextBlock($"第{i}則"));
            ids.Add(message.Id);
            await store.AppendMessageAsync(conversation.Id, message);
        }
        SimulateBeforeBackfill();

        var filled = await NewBackfill(store).RunAsync();

        Assert.Equal(count, filled);
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(SearchTextOf(id))));
        Assert.Equal("第0則", SearchTextOf(ids[0]));
    }

    [Fact]
    public async Task Backfill_LeavesImageAndToolOnlyMessagesEmpty_AndDoesNotRescanThem()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("只有圖片");
        var image = Message(ChatRole.User, new ImageBlock("abc.png", "image/png", "截圖"));
        await store.AppendMessageAsync(conversation.Id, image);
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("有文字")));
        SimulateBeforeBackfill();

        Assert.Equal(1, await NewBackfill(store).RunAsync());

        Assert.Equal(string.Empty, SearchTextOf(image.Id));
        // 旗標記下之後，即使還有空的搜尋文字也不再重掃（純圖片本來就沒有文字）
        Assert.Equal(0, await NewBackfill(store).RunAsync());
    }

    [Fact]
    public async Task Backfill_CanBeCancelled_WithoutSettingTheFlag_SoTheNextRunFinishesTheJob()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("中斷");
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("要補的文字")));
        SimulateBeforeBackfill();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewBackfill(store).RunAsync(cancelled.Token));
        Assert.Empty(await store.SearchAsync("要補的", 10));

        Assert.Equal(1, await NewBackfill(store).RunAsync());
        Assert.Single(await store.SearchAsync("要補的", 10));
    }

    [Fact]
    public async Task Backfill_IsSafeToRerun_ByResettingTheFlag()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("重跑");
        var message = Message(ChatRole.User, new TextBlock("不會被改壞"));
        await store.AppendMessageAsync(conversation.Id, message);
        await NewBackfill(store).RunAsync();
        var before = SearchTextOf(message.Id);

        // 模擬被中斷後又重跑：旗標不在，已經有搜尋文字的列要略過，內容不變
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM app_meta;";
            command.ExecuteNonQuery();
        }
        var rerun = await NewBackfill(store).RunAsync();

        Assert.Equal(0, rerun);
        Assert.Equal(before, SearchTextOf(message.Id));
        Assert.Equal("不會被改壞", before);
    }

    [Fact]
    public async Task NewMessages_AreSearchableWithoutBackfill()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("新的");

        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("剛送出的訊息")));

        Assert.Single(await store.SearchAsync("剛送出", 10));
    }
}
