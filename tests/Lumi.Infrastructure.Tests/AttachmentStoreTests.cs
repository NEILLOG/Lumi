using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-att-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static byte[] PngPlus(byte extra) => [.. Png, extra];

    private string AttachmentsDir => Path.Combine(_directory, "attachments");

    private FileAttachmentStore NewStore() => new(Options.Create(new AttachmentOptions { Directory = AttachmentsDir }));

    private SqliteConversationStore NewDb() => new(Options.Create(new PersistenceOptions { DatabasePath = Path.Combine(_directory, "lumi.db") }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Save_ThenRead_RoundTripsTheBytes()
    {
        var store = NewStore();

        var name = await store.SaveAsync(Png, "image/png");

        Assert.Equal(Png, await store.ReadAsync(name));
        Assert.Matches(@"^[0-9a-f]{64}\.png$", name);
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/webp", ".webp")]
    public async Task Save_UsesTheExtensionOfTheMediaType(string mediaType, string extension) =>
        Assert.EndsWith(extension, await NewStore().SaveAsync(Png, mediaType));

    [Fact]
    public async Task SameContent_IsStoredOnce_UnderTheSameName()
    {
        var store = NewStore();

        var first = await store.SaveAsync(Png, "image/png");
        var second = await store.SaveAsync(Png, "image/png");

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(AttachmentsDir));
    }

    [Fact]
    public async Task DifferentContent_GetsDifferentNames()
    {
        var store = NewStore();

        var a = await store.SaveAsync(Png, "image/png");
        var b = await store.SaveAsync(PngPlus(9), "image/png");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Save_UnsupportedMediaType_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(() => NewStore().SaveAsync(Png, "image/bmp"));

    [Fact]
    public async Task Save_LeavesNoTemporaryFilesBehind()
    {
        await NewStore().SaveAsync(Png, "image/png");

        Assert.DoesNotContain(Directory.GetFiles(AttachmentsDir), f => Path.GetFileName(f).StartsWith(".tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentSavesOfTheSameContent_AllSucceed()
    {
        var store = NewStore();

        var names = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SaveAsync(Png, "image/png")));

        Assert.Single(names.Distinct());
        Assert.Equal(Png, await store.ReadAsync(names[0]));
    }

    [Fact]
    public async Task Save_AFiveMegabyteImage_Works()
    {
        var big = new byte[ImageFormats.MaxBytes];
        Png.CopyTo(big, 0);
        var store = NewStore();

        var name = await store.SaveAsync(big, "image/png");

        Assert.Equal(big.Length, (await store.ReadAsync(name))!.Length);
    }

    [Theory]
    [InlineData("../lumi.db")]
    [InlineData("..\\x.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("")]
    [InlineData("short.png")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000.exe")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000.png/../x")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD.png")]
    public async Task Read_WithAnInvalidOrTraversingName_ReturnsNull_WithoutTouchingOtherFiles(string name)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "x.png"), "不該被讀到");
        await File.WriteAllTextAsync(Path.Combine(_directory, "lumi.db"), "不該被讀到");

        Assert.Null(await NewStore().ReadAsync(name));
    }

    [Fact]
    public async Task Read_AMissingFile_ReturnsNull() =>
        Assert.Null(await NewStore().ReadAsync(new string('a', 64) + ".png"));

    [Fact]
    public async Task Read_WhenTheFolderDoesNotExistYet_ReturnsNull() =>
        Assert.Null(await NewStore().ReadAsync(new string('b', 64) + ".png"));

    // ---- 清理 ----

    [Fact]
    public async Task DeleteUnreferenced_KeepsReferencedFiles_AndRemovesTheRest()
    {
        var store = NewStore();
        var keep = await store.SaveAsync(Png, "image/png");
        var drop = await store.SaveAsync(PngPlus(1), "image/png");

        var deleted = await store.DeleteUnreferencedAsync(new HashSet<string> { keep });

        Assert.Equal(1, deleted);
        Assert.NotNull(await store.ReadAsync(keep));
        Assert.Null(await store.ReadAsync(drop));
    }

    [Fact]
    public async Task DeleteUnreferenced_OnlyTouchesFilesItCreated()
    {
        var store = NewStore();
        await store.SaveAsync(Png, "image/png");
        await File.WriteAllTextAsync(Path.Combine(AttachmentsDir, "README.txt"), "使用者自己放的");
        await File.WriteAllTextAsync(Path.Combine(AttachmentsDir, "notes.png"), "名稱不符合格式");
        await File.WriteAllTextAsync(Path.Combine(AttachmentsDir, ".tmp-leftover"), "殘留的暫存檔");

        var deleted = await store.DeleteUnreferencedAsync(new HashSet<string>());

        Assert.Equal(2, deleted);   // 一個附件、一個暫存檔
        Assert.True(File.Exists(Path.Combine(AttachmentsDir, "README.txt")));
        Assert.True(File.Exists(Path.Combine(AttachmentsDir, "notes.png")));
    }

    [Fact]
    public async Task DeleteUnreferenced_WhenTheFolderDoesNotExist_IsHarmless() =>
        Assert.Equal(0, await NewStore().DeleteUnreferencedAsync(new HashSet<string>()));

    // ---- 引用（資料庫） ----

    private static ChatMessage Message(ChatRole role, params ContentBlock[] blocks) => new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UtcNow);

    [Fact]
    public async Task References_CollectsImageFileNames_AcrossConversations()
    {
        var db = NewDb();
        var one = await db.CreateAsync("1");
        var two = await db.CreateAsync("2");
        await db.AppendMessageAsync(one.Id, Message(ChatRole.User, new ImageBlock("aaa.png", "image/png", "a"), new TextBlock("看")));
        await db.AppendMessageAsync(one.Id, Message(ChatRole.Assistant, new TextBlock("好")));
        await db.AppendMessageAsync(two.Id, Message(ChatRole.User, new ImageBlock("bbb.jpg", "image/jpeg", "b"), new ImageBlock("aaa.png", "image/png", "a"), new TextBlock("再看")));
        await db.AppendMessageAsync(two.Id, Message(ChatRole.User, new TextBlock("文字裡提到 \"type\":\"image\" 也不會被當成引用")));

        var names = await db.GetReferencedFileNamesAsync();

        Assert.Equal(["aaa.png", "bbb.jpg"], names.Order());
    }

    [Fact]
    public async Task References_WithNoImages_IsEmpty()
    {
        var db = NewDb();
        var conversation = await db.CreateAsync("c");
        await db.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock("嗨")));

        Assert.Empty(await db.GetReferencedFileNamesAsync());
    }

    [Fact]
    public async Task Cleaner_RemovesOrphans_AndKeepsWhatMessagesReference()
    {
        var db = NewDb();
        var store = NewStore();
        var conversation = await db.CreateAsync("c");
        var used = await store.SaveAsync(Png, "image/png");
        var orphan = await store.SaveAsync(PngPlus(7), "image/png");
        await db.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new ImageBlock(used, "image/png", "p"), new TextBlock("看")));
        var cleaner = new AttachmentCleaner(store, db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AttachmentCleaner>.Instance);

        var deleted = await cleaner.CleanAsync();

        Assert.Equal(1, deleted);
        Assert.NotNull(await store.ReadAsync(used));
        Assert.Null(await store.ReadAsync(orphan));
    }
}
