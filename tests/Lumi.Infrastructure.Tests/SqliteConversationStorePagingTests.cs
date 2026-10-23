using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>刪除對話（連動）與分頁 <c>ListAsync</c>（游標 (updated_at, id)）。</summary>
public sealed class SqliteConversationStorePagingTests : IDisposable
{
    private const string SameTime = "2026-10-18T09:00:00.0000000Z";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-page-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage Message(params ContentBlock[] content) =>
        new(ChatMessage.NewId(), ChatRole.User, content, DateTimeOffset.UtcNow);

    private long CountRows(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void ForceUpdatedAt(IEnumerable<string> ids, string time)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE conversations SET updated_at = $t WHERE id = $id;";
            command.Parameters.AddWithValue("$t", time);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task Delete_RemovesMessagesAndContextItems_ByCascade()
    {
        var store = NewStore();
        var doomed = await store.CreateAsync("要刪的");
        var keep = await store.CreateAsync("保留的");
        await store.AppendMessageAsync(doomed.Id, Message(new TextBlock("訊息一")));
        await store.AppendMessageAsync(doomed.Id, Message(new TextBlock("訊息二")));
        await store.AppendMessageAsync(keep.Id, Message(new TextBlock("保留")));
        await store.AddAsync(doomed.Id, ContextKind.File, "/a.txt", "a.txt");
        await store.AddAsync(keep.Id, ContextKind.File, "/b.txt", "b.txt");

        var deleted = await store.DeleteAsync(doomed.Id);

        Assert.True(deleted);
        Assert.Equal(0, CountRows($"SELECT COUNT(*) FROM messages WHERE conversation_id = '{doomed.Id}';"));
        Assert.Equal(0, CountRows($"SELECT COUNT(*) FROM conversation_context WHERE conversation_id = '{doomed.Id}';"));
        Assert.Equal(0, CountRows($"SELECT COUNT(*) FROM conversations WHERE id = '{doomed.Id}';"));
        // 其他對話不受影響
        Assert.Single(await store.GetMessagesAsync(keep.Id));
        Assert.Single(await store.GetAsync(keep.Id));
    }

    [Fact]
    public async Task Delete_IsFalse_WhenNothingWasThere_AndTrueOnlyTheFirstTime()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("一次就好");

        Assert.True(await store.DeleteAsync(conversation.Id));
        Assert.False(await store.DeleteAsync(conversation.Id));
        Assert.False(await store.DeleteAsync("no-such-conversation"));
    }

    [Fact]
    public async Task Delete_SurvivesReopen()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("刪掉");
        await store.AppendMessageAsync(conversation.Id, Message(new TextBlock("x")));
        await store.DeleteAsync(conversation.Id);
        SqliteConnection.ClearAllPools();

        Assert.Empty(await NewStore().ListAsync(10));
        Assert.Empty(await NewStore().GetMessagesAsync(conversation.Id));
    }

    [Fact]
    public async Task Paging_WithTheSameUpdatedAt_NeitherRepeatsNorSkips()
    {
        var store = NewStore();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var conversation = await store.CreateAsync($"對話 {i}");
            await store.AppendMessageAsync(conversation.Id, Message(new TextBlock($"第 {i} 則")));
            ids.Add(conversation.Id);
        }
        ForceUpdatedAt(ids, SameTime);

        var seen = new List<string>();
        ConversationCursor? cursor = null;
        while (true)
        {
            var page = await store.ListAsync(2, cursor);
            if (page.Count == 0)
                break;
            seen.AddRange(page.Select(c => c.Id));
            cursor = new ConversationCursor(page[^1].UpdatedAt, page[^1].Id);
        }

        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(5, seen.Count);
        // 相同更新時間時依 id 由大到小，與游標的條件一致
        Assert.Equal(ids.OrderByDescending(x => x, StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task Paging_CursorIsExclusive_AndEndsWithAnEmptyPage()
    {
        var store = NewStore();
        var older = await store.CreateAsync("舊");
        var newer = await store.CreateAsync("新");
        await store.AppendMessageAsync(older.Id, Message(new TextBlock("a")));
        await Task.Delay(5);
        await store.AppendMessageAsync(newer.Id, Message(new TextBlock("b")));

        var first = await store.ListAsync(1);
        var second = await store.ListAsync(1, new ConversationCursor(first[0].UpdatedAt, first[0].Id));
        var third = await store.ListAsync(1, new ConversationCursor(second[0].UpdatedAt, second[0].Id));

        Assert.Equal([newer.Id], first.Select(c => c.Id));
        Assert.Equal([older.Id], second.Select(c => c.Id));
        Assert.Empty(third);
    }

    [Fact]
    public async Task Paging_OnlyListsConversationsWithMessages()
    {
        var store = NewStore();
        var empty = await store.CreateAsync("沒有訊息");
        var withMessage = await store.CreateAsync("有訊息");
        await store.AppendMessageAsync(withMessage.Id, Message(new TextBlock("x")));
        ForceUpdatedAt([empty.Id, withMessage.Id], SameTime);

        var page = await store.ListAsync(10, new ConversationCursor(DateTimeOffset.Parse("2026-10-19T00:00:00Z"), "zzzz"));

        Assert.Equal([withMessage.Id], page.Select(c => c.Id));
    }

    [Fact]
    public async Task Paging_WithoutCursor_MatchesTheOldListOverload()
    {
        var store = NewStore();
        var a = await store.CreateAsync("A");
        var b = await store.CreateAsync("B");
        await store.AppendMessageAsync(a.Id, Message(new TextBlock("a")));
        await store.AppendMessageAsync(b.Id, Message(new TextBlock("b")));

        Assert.Equal((await store.ListAsync(10)).Select(c => c.Id), (await store.ListAsync(10, null)).Select(c => c.Id));
    }
}
