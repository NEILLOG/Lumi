using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

public class ConversationDeletionServiceTests
{
    /// <summary>記錄附件清理被呼叫時的狀態：對話是否還在資料庫、權限記憶是否已清掉。</summary>
    private sealed class ReferencesSpy(Func<Task<bool>> stillInStore, Func<bool> stillAllowed) : IAttachmentReferences
    {
        public int Calls { get; private set; }
        public bool? StoreHadConversation { get; private set; }
        public bool? MemoryStillAllowed { get; private set; }

        public async Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default)
        {
            Calls++;
            StoreHadConversation = await stillInStore();
            MemoryStillAllowed = stillAllowed();
            return new HashSet<string>();
        }
    }

    /// <summary>刪除時丟例外的store，其他成員都委派給內部的假實作。</summary>
    private sealed class FailingDeleteStore(IConversationStore inner) : IConversationStore
    {
        public Task<Conversation> CreateAsync(string title, CancellationToken ct = default) => inner.CreateAsync(title, ct);
        public Task<Conversation?> GetLatestAsync(CancellationToken ct = default) => inner.GetLatestAsync(ct);
        public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default) => inner.ListAsync(limit, before, ct);
        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default) => inner.GetMessagesAsync(conversationId, ct);
        public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => inner.AppendMessageAsync(conversationId, message, ct);
        public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default) => inner.RemoveLastMessageAsync(conversationId, ct);
        public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => inner.RenameAsync(conversationId, title, ct);
        public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default) => inner.SearchAsync(query, limit, ct);
        public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default) => throw new IOException("磁碟壞了");
    }

    [Fact]
    public async Task Delete_RemovesFromStore_ThenClearsPermissions_ThenCleansAttachments()
    {
        var store = new InMemoryConversationStore();
        var memory = new ToolPermissionMemory();
        var conversation = await store.CreateAsync("要刪的");
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("x")], DateTimeOffset.UtcNow));
        memory.Allow(conversation.Id, ToolPermission.Write);
        var spy = new ReferencesSpy(
            async () => (await store.ListAsync(100)).Any(c => c.Id == conversation.Id),
            () => memory.IsAllowed(conversation.Id, ToolPermission.Write));
        var service = new ConversationDeletionService(store, memory, new AttachmentCleaner(new NullAttachmentStore(), spy, NullLogger<AttachmentCleaner>.Instance));

        var deleted = await service.DeleteAsync(conversation.Id);

        Assert.True(deleted);
        Assert.Equal(1, spy.Calls);
        // 清附件的時候：資料庫已經沒有這則對話、權限記憶已經清掉
        Assert.False(spy.StoreHadConversation);
        Assert.False(spy.MemoryStillAllowed);
        Assert.Empty(await store.ListAsync(100));
    }

    [Fact]
    public async Task Delete_OfUnknownConversation_ReturnsFalse()
    {
        var store = new InMemoryConversationStore();
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(),
            new AttachmentCleaner(new NullAttachmentStore(), new ReferencesSpy(() => Task.FromResult(false), () => false), NullLogger<AttachmentCleaner>.Instance));

        Assert.False(await service.DeleteAsync("no-such-conversation"));
    }

    [Fact]
    public async Task WhenStoreDeleteFails_PermissionsAndAttachmentsAreUntouched()
    {
        var inner = new InMemoryConversationStore();
        var conversation = await inner.CreateAsync("刪不掉");
        var memory = new ToolPermissionMemory();
        memory.Allow(conversation.Id, ToolPermission.Write);
        var spy = new ReferencesSpy(() => Task.FromResult(true), () => false);
        var service = new ConversationDeletionService(new FailingDeleteStore(inner), memory,
            new AttachmentCleaner(new NullAttachmentStore(), spy, NullLogger<AttachmentCleaner>.Instance));

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(conversation.Id));

        Assert.Equal(0, spy.Calls);
        Assert.True(memory.IsAllowed(conversation.Id, ToolPermission.Write));
    }

    [Fact]
    public async Task WhenAttachmentCleanupFails_TheConversationIsStillDeleted_AndPermissionsAreCleared()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("附件清理會失敗");
        var memory = new ToolPermissionMemory();
        memory.Allow(conversation.Id, ToolPermission.Write);
        var service = new ConversationDeletionService(store, memory,
            new AttachmentCleaner(new NullAttachmentStore(), new ThrowingReferences(), NullLogger<AttachmentCleaner>.Instance));

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(conversation.Id));

        Assert.Empty(await store.ListAsync(100));
        Assert.False(memory.IsAllowed(conversation.Id, ToolPermission.Write));
    }

    private sealed class ThrowingReferences : IAttachmentReferences
    {
        public Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default) => throw new IOException("讀不到");
    }
}

public class ToolPermissionMemoryClearTests
{
    [Fact]
    public void Clear_RemovesOnlyThatConversation()
    {
        var memory = new ToolPermissionMemory();
        memory.Allow("a", ToolPermission.Write);
        memory.Allow("b", ToolPermission.Network);

        memory.Clear("a");

        Assert.False(memory.IsAllowed("a", ToolPermission.Write));
        Assert.True(memory.IsAllowed("b", ToolPermission.Network));
    }
}
