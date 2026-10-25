using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class ToolResultImageReferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-toolimg-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private FileAttachmentStore NewStore() => new(Options.Create(new AttachmentOptions { Directory = Path.Combine(_directory, "attachments") }));

    private SqliteConversationStore NewDb() => new(Options.Create(new PersistenceOptions { DatabasePath = Path.Combine(_directory, "lumi.db") }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage ToolMessage(params ImageBlock[] images) =>
        new(ChatMessage.NewId(), ChatRole.Tool,
            [new ToolResultBlock("t1", "已截圖", false, "截圖", null, null, 0, ToolApproval.NotRequired, images)], DateTimeOffset.UtcNow);

    [Fact]
    public async Task Images_on_tool_results_are_reported_as_referenced()
    {
        var db = NewDb();
        var conversation = await db.CreateAsync("c");
        await db.AppendMessageAsync(conversation.Id, ToolMessage(new ImageBlock("aaa.png", "image/png", "a"), new ImageBlock("bbb.jpg", "image/jpeg", "b")));

        var names = await db.GetReferencedFileNamesAsync();

        Assert.Equal(["aaa.png", "bbb.jpg"], names.Order());
    }

    [Fact]
    public async Task Tool_results_without_images_reference_nothing()
    {
        var db = NewDb();
        var conversation = await db.CreateAsync("c");
        await db.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
            [new ToolResultBlock("t1", "沒有圖", false, "x", null, null, 0, ToolApproval.NotRequired)], DateTimeOffset.UtcNow));

        Assert.Empty(await db.GetReferencedFileNamesAsync());
    }

    [Fact]
    public async Task The_cleaner_keeps_tool_result_images_until_the_conversation_is_deleted()
    {
        var db = NewDb();
        var store = NewStore();
        var conversation = await db.CreateAsync("c");
        var name = await store.SaveAsync(Png, "image/png");
        await db.AppendMessageAsync(conversation.Id, ToolMessage(new ImageBlock(name, "image/png", "截圖.jpg")));
        var cleaner = new AttachmentCleaner(store, db, NullLogger<AttachmentCleaner>.Instance);

        Assert.Equal(0, await cleaner.CleanAsync());
        Assert.NotNull(await store.ReadAsync(name));

        Assert.True(await db.DeleteAsync(conversation.Id));
        Assert.Equal(1, await cleaner.CleanAsync());
        Assert.Null(await store.ReadAsync(name));
    }
}
