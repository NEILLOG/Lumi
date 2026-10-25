using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Reminders;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests.Memory;

/// <summary>SQLite 版專屬：migration 6、升級不影響既有資料、刪除後 id 不重用（跨連線、跨實例）。</summary>
public sealed class SqliteMemoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T08:00:00+08:00", CultureInfo.InvariantCulture);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-memory-sqlite-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewConversationStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    private SqliteMemoryStore NewStore(MemoryOptions? options = null, TimeProvider? time = null) => new(
        NewConversationStore(),
        options ?? new MemoryOptions(),
        time ?? TimeProvider.System,
        NullLogger<SqliteMemoryStore>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage Message(ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, DateTimeOffset.UtcNow);

    private static ScheduledItem Reminder(string id) => new(
        Id: id,
        Kind: ScheduledKind.Reminder,
        Title: "開會",
        Message: "三點開會",
        Trigger: new OneTimeTrigger(Start.AddHours(7)),
        TimeZoneId: "Asia/Taipei",
        Action: new NotifyAction(),
        Status: ScheduledStatus.Pending,
        Enabled: true,
        NextFireAt: Start.AddHours(7),
        LastFiredAt: null,
        CreatedBy: ScheduledCreator.Agent,
        CreatedAt: Start);

    private int SchemaVersion()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return SqliteSchema.ReadVersion(connection);
    }

    private bool TableExists(string name)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    [Fact]
    public async Task Migration_CreatesTheMemoriesTable_AndLatestVersion()
    {
        await NewStore().ListAsync();

        Assert.Equal(SqliteSchema.LatestVersion, SchemaVersion());
        Assert.True(TableExists("memories"));
    }

    [Fact]
    public void SchemaVersion_IsAtLeast6_BecauseOfMemories() =>
        Assert.True(SqliteSchema.LatestVersion >= 6);

    [Fact]
    public async Task Ids_AreNotReused_AcrossReopeningTheDatabase()
    {
        var first = await NewStore().AddAsync(new NewMemory("甲", MemoryCategory.Profile, MemorySource.Explicit));
        await NewStore().DeleteAsync(first.Item!.Id);

        var reopened = await NewStore().AddAsync(new NewMemory("乙", MemoryCategory.Profile, MemorySource.Explicit));

        Assert.True(reopened.Item!.Id > first.Item!.Id);
    }

    [Fact]
    public async Task Timestamps_RoundTrip_AsUtc()
    {
        var clock = new MemoryTestClock(Start);
        var store = NewStore(time: clock);
        var added = await store.AddAsync(new NewMemory("時間測試", MemoryCategory.Work, MemorySource.Manual, null, new DateOnly(2026, 12, 31)));

        var read = await NewStore().GetAsync(added.Item!.Id);

        Assert.Equal(Start, read!.CreatedAt);
        Assert.Equal(TimeSpan.Zero, read.CreatedAt.Offset);
        Assert.Equal(new DateOnly(2026, 12, 31), read.ExpiresAt);
    }

    [Fact]
    public async Task SourceConversationId_IsNotAForeignKey_DeletingTheConversationKeepsTheMemory()
    {
        var conversations = NewConversationStore();
        var conversation = await conversations.CreateAsync("來源對話");
        var store = NewStore();
        var added = await store.AddAsync(new NewMemory("來自那段對話", MemoryCategory.Profile, MemorySource.Suggested, conversation.Id));

        await conversations.DeleteAsync(conversation.Id);

        Assert.Equal(conversation.Id, (await store.GetAsync(added.Item!.Id))!.SourceConversationId);
    }

    [Fact]
    public async Task UpgradeFromVersion5_AddsMemories_AndKeepsConversationsAndReminders()
    {
        var conversations = NewConversationStore();
        var conversation = await conversations.CreateAsync("M10 的資料");
        await conversations.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("M10 的訊息")));
        var scheduled = new SqliteScheduledItemStore(conversations, NullLogger<SqliteScheduledItemStore>.Instance);
        await scheduled.AddAsync(Reminder("keep-me"));
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // 模擬 migration 6 之前建立的資料庫：沒有 memories，版本是 5
            command.CommandText = "ALTER TABLE messages DROP COLUMN turn_suffix; DROP TABLE IF EXISTS mcp_tool_cache; ALTER TABLE conversations DROP COLUMN tool_snapshot; ALTER TABLE conversations DROP COLUMN system_prompt; DROP TABLE memories; UPDATE schema_version SET version = 5;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopenedConversations = NewConversationStore();
        var messages = await reopenedConversations.GetMessagesAsync(conversation.Id);
        var reminder = await new SqliteScheduledItemStore(reopenedConversations, NullLogger<SqliteScheduledItemStore>.Instance).GetAsync("keep-me");
        var added = await NewStore().AddAsync(new NewMemory("升版後可以新增", MemoryCategory.Profile, MemorySource.Explicit));

        Assert.Equal("M10 的訊息", Assert.IsType<TextBlock>(Assert.Single(messages).Content[0]).Text);
        Assert.NotNull(reminder);
        Assert.Equal("開會", reminder.Title);
        Assert.True(added.Succeeded);
        Assert.Equal(SqliteSchema.LatestVersion, SchemaVersion());
        Assert.True(TableExists("memories"));
    }
}
