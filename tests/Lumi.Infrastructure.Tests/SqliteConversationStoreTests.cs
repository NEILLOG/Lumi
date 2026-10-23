using Lumi.Core.Chat;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-db-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage Message(ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Create_ThenGetLatest_ReturnsIt()
    {
        var store = NewStore();

        var created = await store.CreateAsync("新對話");
        var latest = await store.GetLatestAsync();

        Assert.NotNull(latest);
        Assert.Equal(created.Id, latest.Id);
        Assert.Equal("新對話", latest.Title);
        Assert.True(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task GetLatest_OnEmptyDatabase_ReturnsNull()
    {
        Assert.Null(await NewStore().GetLatestAsync());
    }

    [Fact]
    public async Task GetLatest_FollowsMostRecentlyUpdated()
    {
        var store = NewStore();
        var older = await store.CreateAsync("舊的");
        await store.CreateAsync("新的");

        await Task.Delay(5);
        await store.AppendMessageAsync(older.Id, Message(ChatRole.User, new TextBlock("讓舊的變成最近更新")));

        Assert.Equal(older.Id, (await store.GetLatestAsync())!.Id);
    }

    [Fact]
    public async Task Messages_KeepOrder_AndContentRoundTrips()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("新對話");
        var user = Message(ChatRole.User, new TextBlock("中文與 ❤️‍🔥 🇹🇼"));
        var assistant = new ChatMessage(
            ChatMessage.NewId(), ChatRole.Assistant,
            [
                new ThinkingBlock("先想一下", "opaque-signature=="),
                new RedactedThinkingBlock("redacted-data"),
                new ProviderBlock("anthropic", "fallback", """{"from":{"model":"a"}}"""),
                new TextBlock("回答"),
            ],
            DateTimeOffset.UtcNow, MessageStatus.Complete, "claude-opus-5-5", new TokenUsage(100, 20, 3, 4));
        var stopped = new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("說到一半")],
            DateTimeOffset.UtcNow, MessageStatus.Stopped, "claude-opus-5-5");

        await store.AppendMessageAsync(conversation.Id, user);
        await store.AppendMessageAsync(conversation.Id, assistant);
        await store.AppendMessageAsync(conversation.Id, stopped);

        var messages = await NewStore().GetMessagesAsync(conversation.Id);   // 新的執行個體：確認是從檔案讀回

        Assert.Equal([user.Id, assistant.Id, stopped.Id], messages.Select(m => m.Id));
        Assert.Equal(user.Content, messages[0].Content);
        Assert.Equal(assistant.Content, messages[1].Content);
        Assert.Equal(new TokenUsage(100, 20, 3, 4), messages[1].Usage);
        Assert.Equal("claude-opus-5-5", messages[1].ModelId);
        Assert.Equal(MessageStatus.Stopped, messages[2].Status);
        Assert.Null(messages[2].Usage);
        Assert.Equal(ChatRole.User, messages[0].Role);
    }

    [Fact]
    public async Task CreatedAt_RoundTripsAsUtc()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("新對話");
        var time = new DateTimeOffset(2026, 10, 18, 9, 30, 15, TimeSpan.FromHours(8)).AddTicks(1234567);
        var message = new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("x")], time);

        await store.AppendMessageAsync(conversation.Id, message);
        var read = Assert.Single(await store.GetMessagesAsync(conversation.Id));

        Assert.Equal(time, read.CreatedAt);
        Assert.Equal(TimeSpan.Zero, read.CreatedAt.Offset);
    }

    [Fact]
    public async Task Messages_AreScopedPerConversation()
    {
        var store = NewStore();
        var a = await store.CreateAsync("A");
        var b = await store.CreateAsync("B");

        await store.AppendMessageAsync(a.Id, Message(ChatRole.User, new TextBlock("a1")));
        await store.AppendMessageAsync(b.Id, Message(ChatRole.User, new TextBlock("b1")));
        await store.AppendMessageAsync(a.Id, Message(ChatRole.User, new TextBlock("a2")));

        Assert.Equal(2, (await store.GetMessagesAsync(a.Id)).Count);
        Assert.Single(await store.GetMessagesAsync(b.Id));
    }

    [Fact]
    public async Task Append_ToMissingConversation_Fails()
    {
        var store = NewStore();
        await store.CreateAsync("讓資料庫先建好");

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.AppendMessageAsync("no-such-conversation", Message(ChatRole.User, new TextBlock("x"))));
    }

    [Fact]
    public async Task Rename_ChangesTitle()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("新對話");

        await store.RenameAsync(conversation.Id, "改過的標題");

        Assert.Equal("改過的標題", (await store.GetLatestAsync())!.Title);
    }

    [Fact]
    public async Task Migration_CreatesLatestSchemaVersion()
    {
        await NewStore().CreateAsync("觸發建立");

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        Assert.Equal(SqliteSchema.LatestVersion, SqliteSchema.ReadVersion(connection));
    }

    [Fact]
    public void SchemaVersion_IsAtLeast3_BecauseOfToolRoleAndContext() =>
        Assert.True(SqliteSchema.LatestVersion >= 3);

    [Fact]
    public void SchemaVersion_IsAtLeast4_BecauseOfSearchText() =>
        Assert.True(SqliteSchema.LatestVersion >= 4);

    [Fact]
    public async Task UpgradeFromVersion3_AddsSearchText_AndBackfillFillsOldMessages()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("M9 之前的資料");
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("舊的搜尋字眼")));
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // 模擬 migration 4 之前建立的資料庫：沒有 search_text 與 app_meta，版本是 3
            command.CommandText = "ALTER TABLE messages DROP COLUMN search_text; DROP TABLE app_meta; UPDATE schema_version SET version = 3;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopened = NewStore();
        var messages = await reopened.GetMessagesAsync(conversation.Id);   // 升版後舊訊息讀得回來
        Assert.Equal("舊的搜尋字眼", Assert.IsType<TextBlock>(Assert.Single(messages).Content[0]).Text);
        Assert.Empty(await reopened.SearchAsync("搜尋字眼", 10));   // 補寫之前內文搜不到，只能靠標題

        var filled = await new SearchTextBackfill(reopened, NullLogger<SearchTextBackfill>.Instance).RunAsync();

        Assert.Equal(1, filled);
        Assert.Single(await reopened.SearchAsync("搜尋字眼", 10));
        using var check = new SqliteConnection($"Data Source={DatabasePath}");
        check.Open();
        Assert.Equal(SqliteSchema.LatestVersion, SqliteSchema.ReadVersion(check));
    }

    [Fact]
    public async Task UpgradeFromVersion2_AddsTheContextTable_AndKeepsMessages()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("M3 的資料");
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("M3 的訊息")));
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE conversation_context; ALTER TABLE messages DROP COLUMN search_text; DROP TABLE app_meta; UPDATE schema_version SET version = 2;";   // 模擬 M4 之前建立的資料庫（search_text、app_meta 也是 migration 4 加的）
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopened = NewStore();
        var messages = await reopened.GetMessagesAsync(conversation.Id);
        var added = await reopened.AddAsync(conversation.Id, Lumi.Core.Context.ContextKind.File, "/a.txt", "a.txt");

        Assert.Equal("M3 的訊息", Assert.IsType<TextBlock>(Assert.Single(messages).Content[0]).Text);
        Assert.Equal("/a.txt", added.Path);
    }

    [Fact]
    public async Task ToolMessages_RoundTrip_WithAllFields()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("工具");
        var use = new ToolUseBlock("toolu_1", "calculate", """{"expression":"1+1"}""");
        var result = new ToolResultBlock("toolu_1", "2", false, "計算", "1+1", "= 2", 12, ToolApproval.AllowedForConversation);
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("算")));
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.Assistant, new TextBlock("好"), use));
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.Tool, result));
        SqliteConnection.ClearAllPools();

        var messages = await NewStore().GetMessagesAsync(conversation.Id);

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], messages.Select(m => m.Role));
        Assert.Equal(use, messages[1].Content[1]);
        Assert.Equal(result, Assert.Single(messages[2].Content));
    }

    [Fact]
    public async Task RemoveLastMessage_CanRemoveToolMessage()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("工具");
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("算")));
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.Tool,
            new ToolResultBlock("x", "r", false, "計算", null, null, 0, ToolApproval.NotRequired)));

        Assert.True(await store.RemoveLastMessageAsync(conversation.Id));

        Assert.Equal(ChatRole.User, Assert.Single(await store.GetMessagesAsync(conversation.Id)).Role);
    }

    [Fact]
    public async Task UpgradeFromVersion1_KeepsExistingMessages_AndBumpsVersion()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("舊資料");
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("舊訊息")));
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE conversation_context; ALTER TABLE messages DROP COLUMN search_text; DROP TABLE app_meta; UPDATE schema_version SET version = 1;";   // 模擬 M3 之前建立的資料庫（search_text、app_meta 也是 migration 4 加的）
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var messages = await NewStore().GetMessagesAsync(conversation.Id);

        Assert.Equal("舊訊息", Assert.IsType<TextBlock>(Assert.Single(messages).Content[0]).Text);
        using var check = new SqliteConnection($"Data Source={DatabasePath}");
        check.Open();
        Assert.Equal(SqliteSchema.LatestVersion, SqliteSchema.ReadVersion(check));
    }

    [Fact]
    public async Task Migration_IsIdempotentAcrossInstances()
    {
        var conversation = await NewStore().CreateAsync("第一次");

        SqliteConnection.ClearAllPools();
        var reopened = NewStore();

        Assert.Equal(conversation.Id, (await reopened.GetLatestAsync())!.Id);
    }

    [Fact]
    public async Task NewerDatabaseVersion_IsRefused()
    {
        await NewStore().CreateAsync("建立");
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE schema_version SET version = {SqliteSchema.LatestVersion + 1};";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = await Assert.ThrowsAsync<DatabaseTooNewException>(() => NewStore().GetLatestAsync());
        Assert.Contains("較新版本", ex.Message);
        Assert.Equal(SqliteSchema.LatestVersion + 1, ex.DatabaseVersion);
        Assert.Equal(SqliteSchema.LatestVersion, ex.LatestVersion);
    }

    [Fact]
    public async Task List_ReturnsOnlyConversationsWithMessages_NewestFirst_UpToLimit()
    {
        var store = NewStore();
        var empty = await store.CreateAsync("空的");
        var older = await store.CreateAsync("舊的");
        var newer = await store.CreateAsync("新的");
        await store.AppendMessageAsync(older.Id, Message(ChatRole.User, new TextBlock("a")));
        await Task.Delay(5);
        await store.AppendMessageAsync(newer.Id, Message(ChatRole.User, new TextBlock("b")));

        var all = await store.ListAsync(100);

        Assert.Equal([newer.Id, older.Id], all.Select(c => c.Id));
        Assert.DoesNotContain(all, c => c.Id == empty.Id);
        Assert.Equal([newer.Id], (await store.ListAsync(1)).Select(c => c.Id));
    }

    [Fact]
    public async Task List_OrderFollowsLatestActivity()
    {
        var store = NewStore();
        var a = await store.CreateAsync("A");
        var b = await store.CreateAsync("B");
        await store.AppendMessageAsync(a.Id, Message(ChatRole.User, new TextBlock("1")));
        await Task.Delay(5);
        await store.AppendMessageAsync(b.Id, Message(ChatRole.User, new TextBlock("2")));
        await Task.Delay(5);

        await store.AppendMessageAsync(a.Id, Message(ChatRole.User, new TextBlock("3")));

        Assert.Equal([a.Id, b.Id], (await store.ListAsync(10)).Select(c => c.Id));
    }

    [Fact]
    public async Task RemoveLast_RemovesOnlyTheLastMessage_AndLaterAppendsKeepOrder()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("新對話");
        var first = Message(ChatRole.User, new TextBlock("問"));
        var second = Message(ChatRole.Assistant, new TextBlock("舊答案"));
        await store.AppendMessageAsync(conversation.Id, first);
        await store.AppendMessageAsync(conversation.Id, second);

        Assert.True(await store.RemoveLastMessageAsync(conversation.Id));
        var replacement = Message(ChatRole.Assistant, new TextBlock("新答案"));
        await store.AppendMessageAsync(conversation.Id, replacement);

        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal([first.Id, replacement.Id], messages.Select(m => m.Id));
    }

    [Fact]
    public async Task RemoveLast_DoesNotTouchOtherConversations_NorMoveUpdatedAt()
    {
        var store = NewStore();
        var a = await store.CreateAsync("A");
        var b = await store.CreateAsync("B");
        await store.AppendMessageAsync(a.Id, Message(ChatRole.User, new TextBlock("a")));
        await store.AppendMessageAsync(b.Id, Message(ChatRole.User, new TextBlock("b")));
        var before = (await store.ListAsync(10)).Single(c => c.Id == a.Id).UpdatedAt;

        await store.RemoveLastMessageAsync(b.Id);

        Assert.Single(await store.GetMessagesAsync(a.Id));
        Assert.Empty(await store.GetMessagesAsync(b.Id));
        Assert.Equal(before, (await store.ListAsync(10)).Single(c => c.Id == a.Id).UpdatedAt);
    }

    [Fact]
    public async Task RemoveLast_OnEmptyConversation_ReturnsFalse()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("空的");

        Assert.False(await store.RemoveLastMessageAsync(conversation.Id));
    }
}
