using Lumi.Core.Memory;

namespace Lumi.Core.Tests.TestSupport;

/// <summary>
/// 最小的記憶存取替身：只實作 <see cref="ListActiveAsync"/>（組 prompt 用），其他方法一律丟例外。
/// 測試可以直接改 <see cref="Items"/> 模擬工具寫入。
/// </summary>
internal sealed class FakeMemoryStore : IMemoryStore
{
    public List<MemoryItem> Items { get; } = [];

    /// <summary>被呼叫的次數（驗證每一輪都有讀取）。</summary>
    public int ActiveReads { get; private set; }

    public Task<IReadOnlyList<MemoryItem>> ListActiveAsync(DateOnly today, CancellationToken ct = default)
    {
        ActiveReads++;
        IReadOnlyList<MemoryItem> active = Items
            .Where(i => MemoryRules.IsActive(i, today))
            .OrderBy(i => (int)i.Category).ThenBy(i => i.Id)
            .ToList();
        return Task.FromResult(active);
    }

    public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<MemoryItem?> GetAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
    public MemoryItem? TryGet(int id) => throw new NotSupportedException();
    public Task<MemoryAddResult> AddAsync(NewMemory memory, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<MemoryUpdateResult> UpdateAsync(int id, MemoryPatch patch, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> DeleteAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> PurgeExpiredAsync(DateOnly today, int graceDays, CancellationToken ct = default) => throw new NotSupportedException();
    public CancellationToken ChangeToken => default;
}
