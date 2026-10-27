using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>M22 22J：小精靈專屬對話的 id 管理、工作台啟動時不接手它、清除內容。</summary>
public class SpriteConversationTests
{
    private static ChatMessage Msg(string text) => new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);

    private static ChatSession NewSession(InMemoryConversationStore store, SpriteConversationService sprite) =>
        new(new ConversationService(new ScriptedChatProvider(), store, new FakeModelCatalog()), store, sprite: sprite);

    // ---- SpriteConversationService ----

    [Fact]
    public async Task Get_BeforeItIsCreated_ReturnsNull_AndCreatesNothing()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);

        Assert.Null(await sprite.GetAsync());
        Assert.Null(sprite.CachedId);
        Assert.Null(await store.GetLatestAsync());
    }

    [Fact]
    public async Task GetOrCreate_CreatesOnce_RemembersTheIdInAppMeta_AndKeepsTheTitle()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);

        var first = await sprite.GetOrCreateAsync("與小精靈的對話");
        var second = await sprite.GetOrCreateAsync("別的標題");

        Assert.Equal("與小精靈的對話", first.Title);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, await store.GetMetaAsync(SpriteConversationService.MetaKey));
        Assert.Equal("sprite_conversation_id", SpriteConversationService.MetaKey);
        Assert.True(sprite.IsSpriteConversation(first.Id));
        Assert.False(sprite.IsSpriteConversation("other"));
    }

    [Fact]
    public async Task ANewServiceInstance_FindsTheSameConversation_AfterARestart()
    {
        var store = new InMemoryConversationStore();
        var created = await new SpriteConversationService(store).GetOrCreateAsync("與小精靈的對話");

        var afterRestart = new SpriteConversationService(store);

        Assert.Equal(created.Id, (await afterRestart.GetAsync())?.Id);
        Assert.Equal(created.Id, afterRestart.CachedId);
    }

    [Fact]
    public async Task WhenTheConversationWasDeleted_GetReturnsNull_AndGetOrCreateMakesANewOne()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var first = await sprite.GetOrCreateAsync("與小精靈的對話");
        await store.DeleteAsync(first.Id);

        Assert.Null(await sprite.GetAsync());
        Assert.Null(sprite.CachedId);

        var recreated = await sprite.GetOrCreateAsync("與小精靈的對話");

        Assert.NotEqual(first.Id, recreated.Id);
        Assert.Equal(recreated.Id, await store.GetMetaAsync(SpriteConversationService.MetaKey));
    }

    [Fact]
    public async Task WhenTheStoredIdPointsNowhere_AMissingIdIsRecreated()
    {
        var store = new InMemoryConversationStore();
        await store.SetMetaAsync(SpriteConversationService.MetaKey, "手動改壞的 id");
        var sprite = new SpriteConversationService(store);

        var created = await sprite.GetOrCreateAsync("與小精靈的對話");

        Assert.NotEqual("手動改壞的 id", created.Id);
        Assert.Equal(created.Id, await store.GetMetaAsync(SpriteConversationService.MetaKey));
    }

    // ---- 工作台啟動不接手小精靈的對話 ----

    [Fact]
    public async Task Initialize_SkipsTheSpriteConversation_EvenWhenItIsTheMostRecent()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var work = await store.CreateAsync("工作台的對話");
        await store.AppendMessageAsync(work.Id, Msg("工作"));
        await Task.Delay(5);
        var spriteConversation = await sprite.GetOrCreateAsync("與小精靈的對話");
        await store.AppendMessageAsync(spriteConversation.Id, Msg("嗨"));   // 小精靈的對話最近更新

        var opened = await NewSession(store, sprite).InitializeAsync("新對話");

        Assert.Equal(work.Id, opened.Id);
    }

    [Fact]
    public async Task Initialize_WhenOnlyTheSpriteConversationExists_CreatesAnEmptyWorkbenchConversation()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var spriteConversation = await sprite.GetOrCreateAsync("與小精靈的對話");
        await store.AppendMessageAsync(spriteConversation.Id, Msg("嗨"));

        var opened = await NewSession(store, sprite).InitializeAsync("新對話");

        Assert.NotEqual(spriteConversation.Id, opened.Id);
        Assert.Equal("新對話", opened.Title);
    }

    [Fact]
    public async Task Initialize_WithoutASpriteConversation_StillOpensTheLatest()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var work = await store.CreateAsync("工作台的對話");

        var opened = await NewSession(store, sprite).InitializeAsync("新對話");

        Assert.Equal(work.Id, opened.Id);
    }

    // ---- 清除內容 ----

    [Fact]
    public async Task Clear_RemovesMessagesAndCompactions_ButKeepsTheConversationIdAndTitle()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var conversation = await sprite.GetOrCreateAsync("與小精靈的對話");
        await store.RenameAsync(conversation.Id, "改過的名字");
        await store.AppendMessageAsync(conversation.Id, Msg("一"));
        await store.AppendMessageAsync(conversation.Id, Msg("二"));
        await store.AddCompactionAsync(conversation.Id, new CompactionRecord(CompactionRecord.NewId(), CompactionKind.Summary, 1, "摘要", null, DateTimeOffset.UtcNow));
        await store.SetSystemPromptAsync(conversation.Id, "system");
        await store.SetToolSnapshotAsync(conversation.Id, "{}");
        var memory = new ToolPermissionMemory();
        memory.Allow(conversation.Id, ToolPermission.Write);
        var deletion = new ConversationDeletionService(store, memory,
            new AttachmentCleaner(new NullAttachmentStore(), new NoReferences(), NullLogger<AttachmentCleaner>.Instance));

        await deletion.ClearAsync(conversation.Id);

        Assert.Empty(await store.GetMessagesAsync(conversation.Id));
        Assert.True((await store.GetCompactionStateAsync(conversation.Id)).IsEmpty);
        Assert.Null(await store.GetSystemPromptAsync(conversation.Id));
        Assert.Null(await store.GetToolSnapshotAsync(conversation.Id));
        Assert.False(memory.IsAllowed(conversation.Id, ToolPermission.Write));
        var kept = await store.GetConversationAsync(conversation.Id);
        Assert.Equal("改過的名字", kept?.Title);
        Assert.Equal(conversation.Id, await sprite.GetIdAsync());
    }

    private sealed class NoReferences : IAttachmentReferences
    {
        public Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }
}
