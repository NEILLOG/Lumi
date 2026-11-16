using System.Collections.Concurrent;

namespace Lumi.Core.Branches;

/// <summary>
/// <see cref="IBranchStore"/> 的記憶體版（測試與預設）。排序與「只在 Active 時改」照介面的註解；
/// 沒有對話表，所以 <see cref="ListOrphanConversationsAsync"/> 回空清單，刪除對話時的串連刪除用 <see cref="Remove"/> 模擬。
/// </summary>
public sealed class InMemoryBranchStore : IBranchStore
{
    private readonly ConcurrentDictionary<string, Branch> _branches = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public Task AddAsync(Branch branch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(branch);
        if (!_branches.TryAdd(branch.ConversationId, branch))
            throw new InvalidOperationException("這則對話已經是支線。");
        return Task.CompletedTask;
    }

    public Task<Branch?> GetAsync(string conversationId, CancellationToken ct = default) =>
        Task.FromResult(_branches.GetValueOrDefault(conversationId));

    public Task<IReadOnlyList<Branch>> ListChildrenAsync(string parentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Branch>>(_branches.Values.Where(b => b.ParentId == parentId)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.ConversationId, StringComparer.Ordinal).ToArray());

    public Task<IReadOnlyList<Branch>> ListByRootsAsync(IReadOnlyCollection<string> rootIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootIds);
        var roots = rootIds.ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<Branch>>(_branches.Values.Where(b => roots.Contains(b.RootId))
            .OrderBy(b => b.RootId, StringComparer.Ordinal).ThenBy(b => b.CreatedAt).ThenBy(b => b.ConversationId, StringComparer.Ordinal).ToArray());
    }

    public Task<bool> MergeAsync(string conversationId, string summary, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_branches.TryGetValue(conversationId, out var branch) || branch.Status != BranchStatus.Active)
                return Task.FromResult(false);
            _branches[conversationId] = branch with { Status = BranchStatus.Merged, Summary = summary, EndedAt = endedAt };
            return Task.FromResult(true);
        }
    }

    public Task<bool> AbandonAsync(string conversationId, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_branches.TryGetValue(conversationId, out var branch) || branch.Status != BranchStatus.Active)
                return Task.FromResult(false);
            _branches[conversationId] = branch with { Status = BranchStatus.Abandoned, EndedAt = endedAt };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<Branch>> ListUndeliveredAsync(string parentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Branch>>(_branches.Values
            .Where(b => b.ParentId == parentId && b.Status == BranchStatus.Merged && b.DeliveredMessageId is null)
            .OrderBy(b => b.EndedAt).ThenBy(b => b.ConversationId, StringComparer.Ordinal).ToArray());

    public Task MarkDeliveredAsync(IReadOnlyCollection<string> conversationIds, string messageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        lock (_lock)
        {
            foreach (var id in conversationIds)
                if (_branches.TryGetValue(id, out var branch) && branch.DeliveredMessageId is null)
                    _branches[id] = branch with { DeliveredMessageId = messageId };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListOrphanConversationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>測試用：模擬刪掉這則對話時資料庫串連刪除的列（自己的、以它為上一層或主線的）。</summary>
    public void Remove(string conversationId)
    {
        foreach (var branch in _branches.Values.Where(b => b.ConversationId == conversationId || b.ParentId == conversationId || b.RootId == conversationId).ToArray())
            _branches.TryRemove(branch.ConversationId, out _);
    }
}
