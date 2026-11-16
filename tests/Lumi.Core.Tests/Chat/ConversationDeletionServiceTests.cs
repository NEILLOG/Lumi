using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.ContextNotes;
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

    /// <summary>研究本資料夾的假實作：刪除時先呼叫 <c>onDelete</c>（可以記錄或丟例外），其他成員委派給記憶體版。</summary>
    private sealed class NoteFilesFake(Func<string, Task> onDelete) : IContextNoteFiles
    {
        private readonly InMemoryContextNoteFiles _inner = new();

        public InMemoryContextNoteFiles Inner => _inner;
        public int DeleteCalls { get; private set; }

        public string GetFolder(string noteId) => _inner.GetFolder(noteId);
        public Task<string?> ReadNoteAsync(string noteId, CancellationToken ct = default) => _inner.ReadNoteAsync(noteId, ct);
        public Task WriteNoteAsync(string noteId, string fileText, CancellationToken ct = default) => _inner.WriteNoteAsync(noteId, fileText, ct);
        public Task<string> SaveImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default) =>
            _inner.SaveImageAsync(noteId, data, mediaType, ct);
        public string? ResolveImagePath(string noteId, string relativePath) => _inner.ResolveImagePath(noteId, relativePath);
        public Task ExportZipAsync(string noteId, string destinationZipPath, CancellationToken ct = default) =>
            _inner.ExportZipAsync(noteId, destinationZipPath, ct);

        public async Task DeleteAsync(string noteId, CancellationToken ct = default)
        {
            DeleteCalls++;
            await onDelete(noteId).ConfigureAwait(false);
            await _inner.DeleteAsync(noteId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>查詢研究本時丟例外的假實作；其他成員不會被這個測試呼叫。</summary>
    private sealed class ThrowingNoteStore : IContextNoteStore
    {
        public Task<ContextNote?> GetByConversationAsync(string conversationId, CancellationToken ct = default) => throw new IOException("讀不到");
        public Task<ContextNote?> GetAsync(string noteId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContextNoteRevision?> GetRevisionAsync(string noteId, int version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContextNoteRevisionInfo>> ListRevisionsAsync(string noteId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateAsync(ContextNote note, ContextNoteRevision first, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddRevisionAsync(ContextNoteRevision revision, ContextNoteProgress? progress = null, bool setGoalHint = false, string? goalHint = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetProgressAsync(string noteId, ContextNoteProgress progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetGoalHintAsync(string noteId, string? goalHint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContextNoteSuggestion?> GetSuggestionAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryMarkSuggestedAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DismissSuggestionAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default) => throw new NotSupportedException();
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

    [Fact]
    public async Task WithANote_TheFolderIsDeleted_AfterTheConversationIsGone()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("有研究本");
        var notes = new InMemoryContextNoteStore();
        var note = new ContextNote(ContextNote.NewId(), conversation.Id, "目標", true, 1, 0, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await notes.CreateAsync(note, new ContextNoteRevision(ContextNoteRevision.NewId(), note.Id, 1, ContextNoteRevisionKind.StartBlank,
            "目標", "## 已決定\n\n- 用 SQLite\n", [], null, 0, DateTimeOffset.UtcNow));
        var storeHadConversation = (bool?)null;
        var files = new NoteFilesFake(async _ => storeHadConversation = (await store.ListAsync(100)).Any(c => c.Id == conversation.Id));
        await files.WriteNoteAsync(note.Id, "# 研究本");
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(),
            new AttachmentCleaner(new NullAttachmentStore(), new ReferencesSpy(() => Task.FromResult(false), () => false), NullLogger<AttachmentCleaner>.Instance),
            notes: notes, files: files);

        Assert.True(await service.DeleteAsync(conversation.Id));

        Assert.Equal(1, files.DeleteCalls);
        Assert.False(storeHadConversation);   // 資料庫刪完之後才刪資料夾
        Assert.Null(await files.ReadNoteAsync(note.Id));
    }

    [Fact]
    public async Task WhenTheFolderCannotBeDeleted_TheConversationIsStillDeleted_AndTheResultIsTrue()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("資料夾刪不掉");
        var notes = new InMemoryContextNoteStore();
        var note = new ContextNote(ContextNote.NewId(), conversation.Id, "目標", true, 1, 0, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await notes.CreateAsync(note, new ContextNoteRevision(ContextNoteRevision.NewId(), note.Id, 1, ContextNoteRevisionKind.StartBlank,
            "目標", "", [], null, 0, DateTimeOffset.UtcNow));
        var files = new NoteFilesFake(_ => throw new IOException("資料夾被佔用"));
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(),
            new AttachmentCleaner(new NullAttachmentStore(), new ReferencesSpy(() => Task.FromResult(false), () => false), NullLogger<AttachmentCleaner>.Instance),
            notes: notes, files: files);

        Assert.True(await service.DeleteAsync(conversation.Id));

        Assert.Equal(1, files.DeleteCalls);
        Assert.Empty(await store.ListAsync(100));
    }

    [Fact]
    public async Task WithoutANote_NoFolderIsTouched()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("沒有研究本");
        var files = new NoteFilesFake(_ => Task.CompletedTask);
        await files.WriteNoteAsync("別的研究本", "# 不相干");
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(),
            new AttachmentCleaner(new NullAttachmentStore(), new ReferencesSpy(() => Task.FromResult(false), () => false), NullLogger<AttachmentCleaner>.Instance),
            notes: new InMemoryContextNoteStore(), files: files);

        Assert.True(await service.DeleteAsync(conversation.Id));

        Assert.Equal(0, files.DeleteCalls);
        Assert.Equal("# 不相干", await files.ReadNoteAsync("別的研究本"));
    }

    [Fact]
    public async Task WhenTheNoteLookupFails_TheConversationIsStillDeleted()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("查詢會失敗");
        var files = new NoteFilesFake(_ => Task.CompletedTask);
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(),
            new AttachmentCleaner(new NullAttachmentStore(), new ReferencesSpy(() => Task.FromResult(false), () => false), NullLogger<AttachmentCleaner>.Instance),
            notes: new ThrowingNoteStore(), files: files);

        Assert.True(await service.DeleteAsync(conversation.Id));

        Assert.Empty(await store.ListAsync(100));
        Assert.Equal(0, files.DeleteCalls);   // 查不到就當成沒有研究本
    }

    private static readonly DateTimeOffset BranchNow = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>一條進行中的支線列（對話本身要先建好）。</summary>
    private static Branch BranchRow(string childId, string parentId, string rootId, int depth) =>
        new(childId, parentId, rootId, depth, "fork", "摘錄", BranchNow, null, BranchStatus.Active, null, null, BranchNow, null);

    [Fact]
    public async Task Deleting_root_deletes_both_levels_of_branches()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var first = await store.CreateAsync("第一層", ConversationKind.Branch);
        var second = await store.CreateAsync("第二層", ConversationKind.Branch);
        await branches.AddAsync(BranchRow(first.Id, main.Id, main.Id, 1));
        await branches.AddAsync(BranchRow(second.Id, first.Id, main.Id, 2));
        var spy = new ReferencesSpy(() => Task.FromResult(false), () => false);
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(),
            new AttachmentCleaner(new NullAttachmentStore(), spy, NullLogger<AttachmentCleaner>.Instance), branches: branches);

        Assert.True(await service.DeleteAsync(main.Id));

        Assert.Null(await store.GetConversationAsync(main.Id));
        Assert.Null(await store.GetConversationAsync(first.Id));
        Assert.Null(await store.GetConversationAsync(second.Id));
        Assert.Equal(1, spy.Calls);   // 附件清理只在最後做一次
    }

    [Fact]
    public async Task Branch_notes_and_memory_are_cleared()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var child = await store.CreateAsync("支線", ConversationKind.Branch);
        await branches.AddAsync(BranchRow(child.Id, main.Id, main.Id, 1));

        // 支線自己的研究本與權限記憶：刪主線時一起清掉
        var notes = new InMemoryContextNoteStore();
        var note = new ContextNote(ContextNote.NewId(), child.Id, "目標", true, 1, 0, null, null, BranchNow, BranchNow);
        await notes.CreateAsync(note, new ContextNoteRevision(ContextNoteRevision.NewId(), note.Id, 1, ContextNoteRevisionKind.StartBlank,
            "目標", "", [], null, 0, BranchNow));
        var memory = new ToolPermissionMemory();
        memory.Allow(child.Id, ToolPermission.Write);
        var files = new NoteFilesFake(_ => Task.CompletedTask);
        var spy = new ReferencesSpy(() => Task.FromResult(false), () => false);
        var service = new ConversationDeletionService(store, memory,
            new AttachmentCleaner(new NullAttachmentStore(), spy, NullLogger<AttachmentCleaner>.Instance),
            notes: notes, files: files, branches: branches);

        await service.DeleteAsync(main.Id);

        Assert.False(memory.IsAllowed(child.Id, ToolPermission.Write));
        Assert.Equal(1, files.DeleteCalls);
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
