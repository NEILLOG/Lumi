using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.TestSupport;

/// <summary>歷史抽屜測試用的宿主：只記錄工作台被要求做的事（真正的切換在 WorkbenchViewModel 的測試裡驗證）。</summary>
internal sealed class FakeHistoryHost : IHistoryHost
{
    public string? CurrentConversationId { get; set; }

    public bool IsResponding { get; set; }

    public List<string> Opened { get; } = [];

    public List<(string Id, string? NextId)> Deleted { get; } = [];

    public List<(string Id, string Title)> Renamed { get; } = [];

    public Task OpenAsync(Conversation conversation)
    {
        Opened.Add(conversation.Id);
        CurrentConversationId = conversation.Id;
        return Task.CompletedTask;
    }

    public Task ConversationDeletedAsync(string conversationId, Conversation? next)
    {
        Deleted.Add((conversationId, next?.Id));
        return Task.CompletedTask;
    }

    public void TitleChanged(string conversationId, string title) => Renamed.Add((conversationId, title));

    public List<string> Cleared { get; } = [];

    public Task ConversationClearedAsync(string conversationId)
    {
        Cleared.Add(conversationId);
        return Task.CompletedTask;
    }
}

/// <summary>沒有任何引用的附件替身（刪除流程的附件清理用）。</summary>
internal sealed class NoAttachmentReferences : IAttachmentReferences
{
    public Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// 可以指定每則對話的更新時間的對話儲存（分組、分頁、搜尋節流測試用）。只實作歷史抽屜會用到的成員；
/// 搜尋可以用 <see cref="SearchHook"/> 攔截（決定何時完成、是否理會取消）。
/// </summary>
internal sealed class StaticListStore : IConversationStore
{
    private readonly object _lock = new();
    private readonly List<Conversation> _items = [];

    /// <summary>非 null 時搜尋改由它處理。</summary>
    public Func<string, int, CancellationToken, Task<IReadOnlyList<ConversationSearchHit>>>? SearchHook { get; set; }

    public List<string> SearchedQueries { get; } = [];

    public void Add(string id, string title, DateTimeOffset updatedAt)
    {
        lock (_lock)
            _items.Add(new Conversation(id, title, updatedAt, updatedAt));
    }

    public IReadOnlyList<Conversation> All()
    {
        lock (_lock)
            return _items.ToList();
    }

    public Conversation Get(string id)
    {
        lock (_lock)
            return _items.Single(c => c.Id == id);
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<Conversation> page = _items
                .Where(c => before is null || c.UpdatedAt < before.UpdatedAt
                            || (c.UpdatedAt == before.UpdatedAt && string.CompareOrdinal(c.Id, before.Id) < 0))
                .OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id, StringComparer.Ordinal)
                .Take(limit).ToList();
            return Task.FromResult(page);
        }
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, CancellationToken ct = default) => ListAsync(limit, null, ct);

    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        SearchedQueries.Add(query);
        if (SearchHook is { } hook)
            return hook(query, limit, ct);
        lock (_lock)
        {
            IReadOnlyList<ConversationSearchHit> hits = _items
                .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.UpdatedAt)
                .Take(limit)
                .Select(c => new ConversationSearchHit(c, ""))
                .ToList();
            return Task.FromResult(hits);
        }
    }

    public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_items.RemoveAll(c => c.Id == conversationId) > 0);
    }

    public Task RenameAsync(string conversationId, string title, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var index = _items.FindIndex(c => c.Id == conversationId);
            if (index >= 0)
                _items[index] = _items[index] with { Title = title };
        }
        return Task.CompletedTask;
    }

    public Task<Conversation> CreateAsync(string title, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<Conversation?> GetLatestAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => throw new NotSupportedException();
}

internal static class HistoryTestSupport
{
    /// <summary>真正的刪除服務（資料庫 → 權限記憶 → 附件清理），附件用空替身。</summary>
    public static ConversationDeletionService Deletion(IConversationStore store) =>
        new(store, new ToolPermissionMemory(),
            new AttachmentCleaner(new NullAttachmentStore(), new NoAttachmentReferences(), NullLogger<AttachmentCleaner>.Instance));

    /// <summary>自訂時區（不依賴系統時區資料庫，Windows 與 macOS 的名稱不同）。</summary>
    public static TimeZoneInfo Zone(int hours) =>
        TimeZoneInfo.CreateCustomTimeZone($"UTC{hours:+0;-0}", TimeSpan.FromHours(hours), $"UTC{hours:+0;-0}", $"UTC{hours:+0;-0}");
}
