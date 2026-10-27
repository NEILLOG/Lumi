using Lumi.Core.Chat;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M22 22J：<c>app_meta</c> 的讀寫、依 id 取對話、清除內容（訊息、整理紀錄、凍結的 prompt），保留對話本身。</summary>
public sealed class SqliteSpriteConversationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-sprite-conv-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "lumi.db");

    private SqliteConversationStore NewStore() => new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage Message(string text) => new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);

    [Fact]
    public async Task Meta_ReadsNullWhenMissing_WritesAndOverwrites_AndSurvivesANewStore()
    {
        var store = NewStore();

        Assert.Null(await store.GetMetaAsync("sprite_conversation_id"));

        await store.SetMetaAsync("sprite_conversation_id", "a");
        await store.SetMetaAsync("sprite_conversation_id", "b");

        Assert.Equal("b", await NewStore().GetMetaAsync("sprite_conversation_id"));
    }

    [Fact]
    public async Task GetConversation_ReturnsItEvenWithoutMessages_AndNullWhenMissing()
    {
        var store = NewStore();
        var created = await store.CreateAsync("與小精靈的對話");

        var found = await store.GetConversationAsync(created.Id);

        Assert.Equal("與小精靈的對話", found?.Title);
        Assert.Null(await store.GetConversationAsync("no-such-id"));
    }

    [Fact]
    public async Task ClearMessages_RemovesMessagesCompactionsAndFrozenPrompts_ButKeepsTheConversation()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("與小精靈的對話");
        var other = await store.CreateAsync("別的對話");
        foreach (var id in new[] { conversation.Id, other.Id })
        {
            await store.AppendMessageAsync(id, Message("一"));
            await store.AppendMessageAsync(id, Message("二"));
            await store.AddCompactionAsync(id, new CompactionRecord(CompactionRecord.NewId(), CompactionKind.Summary, 1, "摘要", "m", DateTimeOffset.UtcNow));
            await store.SetSystemPromptAsync(id, "system");
            await store.SetToolSnapshotAsync(id, "{}");
        }
        var updatedBefore = (await store.GetConversationAsync(conversation.Id))!.UpdatedAt;

        await store.ClearMessagesAsync(conversation.Id);

        Assert.Empty(await store.GetMessagesAsync(conversation.Id));
        Assert.True((await store.GetCompactionStateAsync(conversation.Id)).IsEmpty);
        Assert.Null(await store.GetSystemPromptAsync(conversation.Id));
        Assert.Null(await store.GetToolSnapshotAsync(conversation.Id));
        var kept = await store.GetConversationAsync(conversation.Id);
        Assert.Equal("與小精靈的對話", kept?.Title);
        Assert.Equal(updatedBefore, kept?.UpdatedAt);
        // 別的對話完全不受影響
        Assert.Equal(2, (await store.GetMessagesAsync(other.Id)).Count);
        Assert.False((await store.GetCompactionStateAsync(other.Id)).IsEmpty);
        Assert.Equal("system", await store.GetSystemPromptAsync(other.Id));
    }

    [Fact]
    public async Task AfterClearing_NewMessagesStartAgainFromSeqOne()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("c");
        await store.AppendMessageAsync(conversation.Id, Message("舊的"));
        await store.ClearMessagesAsync(conversation.Id);

        await store.AppendMessageAsync(conversation.Id, Message("新的"));

        Assert.Equal([1], (await store.GetMessagesAsync(conversation.Id)).Select(m => m.Seq));
    }
}
