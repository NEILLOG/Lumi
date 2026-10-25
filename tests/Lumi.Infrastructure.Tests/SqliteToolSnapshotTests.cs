using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M15 15A：對話的工具快照（<c>conversations.tool_snapshot</c>，migration 9）存取。</summary>
public sealed class SqliteToolSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-toolsnap-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task New_conversation_has_no_snapshot()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("新對話");

        Assert.Null(await store.GetToolSnapshotAsync(conversation.Id));
        Assert.Null(await store.GetToolSnapshotAsync("不存在"));
    }

    [Fact]
    public async Task Snapshot_round_trips()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("工具快照");
        const string snapshot = """[{"name":"add_note","description":"把一件事記到今天的筆記。","input_schema":{"type":"object"}}]""";

        await store.SetToolSnapshotAsync(conversation.Id, snapshot);

        Assert.Equal(snapshot, await NewStore().GetToolSnapshotAsync(conversation.Id));   // 重開之後仍在
    }

    [Fact]
    public async Task Snapshot_IsRememberedPerConversation()
    {
        var store = NewStore();
        var first = await store.CreateAsync("一");
        var second = await store.CreateAsync("二");

        await store.SetToolSnapshotAsync(first.Id, "[]");

        Assert.Equal("[]", await store.GetToolSnapshotAsync(first.Id));
        Assert.Null(await store.GetToolSnapshotAsync(second.Id));
    }

    [Fact]
    public async Task Snapshot_CanBeReplaced()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("替換");

        await store.SetToolSnapshotAsync(conversation.Id, "[1]");
        await store.SetToolSnapshotAsync(conversation.Id, "[2]");

        Assert.Equal("[2]", await NewStore().GetToolSnapshotAsync(conversation.Id));
    }
}
