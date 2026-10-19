using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class SqliteContextStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-ctx-tests-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "lumi.db");

    private SqliteConversationStore NewStore() => new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Add_ThenGet_ReturnsItemsInTheOrderTheyWereAdded()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");

        await store.AddAsync(conversation.Id, ContextKind.File, "/b.txt", "b.txt");
        await Task.Delay(5);
        await store.AddAsync(conversation.Id, ContextKind.Folder, "/proj", "proj");

        var items = await store.GetAsync(conversation.Id);
        Assert.Equal(["/b.txt", "/proj"], items.Select(i => i.Path));
        Assert.Equal([ContextKind.File, ContextKind.Folder], items.Select(i => i.Kind));
        Assert.All(items, i => Assert.True(i.IsPending));
    }

    [Fact]
    public async Task Add_TheSameItemTwice_DoesNotDuplicate_AndKeepsTheSameId()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");

        var first = await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");
        var second = await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await store.GetAsync(conversation.Id));
    }

    [Fact]
    public async Task Add_AnAlreadyEmbeddedItem_BecomesPendingAgain()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");
        var item = await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");
        await store.MarkEmbeddedAsync(conversation.Id, [item.Id], "m1");
        Assert.Equal("m1", (await store.GetAsync(conversation.Id))[0].EmbeddedInMessageId);

        var again = await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");   // 再拍一次新快照

        Assert.Equal(item.Id, again.Id);
        Assert.True(again.IsPending);
        Assert.True((await store.GetAsync(conversation.Id))[0].IsPending);
    }

    [Fact]
    public async Task Add_APendingItemAgain_LeavesItPending_WithoutTouchingOthers()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");
        var a = await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");
        var b = await store.AddAsync(conversation.Id, ContextKind.File, "/b.txt", "b.txt");
        await store.MarkEmbeddedAsync(conversation.Id, [a.Id], "m1");

        await store.AddAsync(conversation.Id, ContextKind.File, "/b.txt", "b.txt");

        var items = await store.GetAsync(conversation.Id);
        Assert.Equal("m1", items.Single(i => i.Id == a.Id).EmbeddedInMessageId);
        Assert.True(items.Single(i => i.Id == b.Id).IsPending);
    }

    [Fact]
    public async Task SamePath_DifferentKind_AreSeparateItems()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");

        await store.AddAsync(conversation.Id, ContextKind.File, "/x", "x");
        await store.AddAsync(conversation.Id, ContextKind.Folder, "/x", "x");

        Assert.Equal(2, (await store.GetAsync(conversation.Id)).Count);
    }

    [Fact]
    public async Task Remove_DeletesOnlyThatItem()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");
        var a = await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");
        await store.AddAsync(conversation.Id, ContextKind.File, "/b.txt", "b.txt");

        await store.RemoveAsync(conversation.Id, a.Id);

        Assert.Equal(["/b.txt"], (await store.GetAsync(conversation.Id)).Select(i => i.Path));
    }

    [Fact]
    public async Task Remove_AnUnknownItem_IsHarmless()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");

        await store.RemoveAsync(conversation.Id, "nope");

        Assert.Empty(await store.GetAsync(conversation.Id));
    }

    [Fact]
    public async Task MarkEmbedded_SetsTheMessageId_ForOnlyTheGivenItems()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");
        var a = await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");
        var b = await store.AddAsync(conversation.Id, ContextKind.File, "/b.txt", "b.txt");
        await store.AddAsync(conversation.Id, ContextKind.Image, "/c.png", "c.png");

        await store.MarkEmbeddedAsync(conversation.Id, [a.Id, b.Id], "m9");

        var items = await store.GetAsync(conversation.Id);
        Assert.Equal(["m9", "m9", null], items.Select(i => i.EmbeddedInMessageId));
    }

    [Fact]
    public async Task MarkEmbedded_WithNoItems_DoesNothing()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");

        await store.MarkEmbeddedAsync(conversation.Id, [], "m1");

        Assert.Empty(await store.GetAsync(conversation.Id));
    }

    [Fact]
    public async Task Items_AreIsolatedPerConversation()
    {
        var store = NewStore();
        var one = await store.CreateAsync("1");
        var two = await store.CreateAsync("2");

        await store.AddAsync(one.Id, ContextKind.File, "/a.txt", "a.txt");
        await store.AddAsync(two.Id, ContextKind.File, "/a.txt", "a.txt");   // 另一則對話可以加入同一個檔案
        var other = (await store.GetAsync(two.Id)).Single();
        await store.RemoveAsync(one.Id, other.Id);   // 用別的對話的 id 不能刪掉

        Assert.Single(await store.GetAsync(one.Id));
        Assert.Single(await store.GetAsync(two.Id));
    }

    [Fact]
    public async Task ItemsSurviveReopening_WithUnicodePaths()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");
        await store.AddAsync(conversation.Id, ContextKind.Folder, "/Users/我/專案 📁/src", "src");
        SqliteConnection.ClearAllPools();

        var items = await NewStore().GetAsync(conversation.Id);

        var item = Assert.Single(items);
        Assert.Equal("/Users/我/專案 📁/src", item.Path);
        Assert.Equal(ContextKind.Folder, item.Kind);
    }

    [Fact]
    public async Task DeletingAConversation_RemovesItsItems()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");
        await store.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", conversation.Id);
            command.ExecuteNonQuery();
        }

        using var check = new SqliteConnection($"Data Source={DatabasePath}");
        check.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM conversation_context;";
        Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public async Task OneInstance_ImplementsBothInterfaces_AndTheLatestSchemaHasTheTable()
    {
        var store = NewStore();
        await store.CreateAsync("c");

        Assert.IsAssignableFrom<IConversationStore>(store);
        Assert.IsAssignableFrom<IConversationContextStore>(store);
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        Assert.Equal(SqliteSchema.LatestVersion, SqliteSchema.ReadVersion(connection));
    }
}
