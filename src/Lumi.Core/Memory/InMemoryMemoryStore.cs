namespace Lumi.Core.Memory;

/// <summary>
/// <see cref="IMemoryStore"/> 的記憶體版本，給測試與替身用。行為與 <c>SqliteMemoryStore</c> 一致（規則走 <see cref="MemoryWriteRules"/>）。
/// 執行緒安全（一把鎖）；id 從 1 開始遞增，刪除後不重用。
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly object _lock = new();
    private readonly Dictionary<int, MemoryItem> _items = [];
    private readonly MemoryOptions _options;
    private readonly TimeProvider _time;
    private int _lastId;

    private readonly object _changeLock = new();
    private CancellationTokenSource _changes = new();

    public InMemoryMemoryStore(MemoryOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
    }

    /// <inheritdoc />
    public CancellationToken ChangeToken
    {
        get
        {
            lock (_changeLock)
                return _changes.Token;
        }
    }

    public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(Sorted(_items.Values));
    }

    public Task<IReadOnlyList<MemoryItem>> ListActiveAsync(DateOnly today, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(Sorted(_items.Values.Where(item => MemoryRules.IsActive(item, today))));
    }

    public Task<MemoryItem?> GetAsync(int id, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_items.GetValueOrDefault(id));
    }

    public MemoryItem? TryGet(int id)
    {
        lock (_lock)
            return _items.GetValueOrDefault(id);
    }

    public Task<MemoryAddResult> AddAsync(NewMemory memory, CancellationToken ct = default)
    {
        MemoryAddResult result;
        lock (_lock)
        {
            var normalized = MemoryRules.NormalizeContent(memory.Content);
            var rejected = MemoryWriteRules.CheckAdd(normalized, memory.Category, memory.Source, _items.Values.ToList(), _options);
            if (rejected is not null)
                return Task.FromResult(rejected);

            var now = _time.GetUtcNow();
            var item = new MemoryItem(
                Id: ++_lastId,
                Content: normalized,
                Category: memory.Category,
                Source: memory.Source,
                SourceConversationId: memory.SourceConversationId,
                CreatedAt: now,
                UpdatedAt: now,
                ExpiresAt: memory.ExpiresAt);
            _items[item.Id] = item;
            result = new MemoryAddResult(MemoryAddOutcome.Added, item, null);
        }
        SignalChange();
        return Task.FromResult(result);
    }

    public Task<MemoryUpdateResult> UpdateAsync(int id, MemoryPatch patch, CancellationToken ct = default)
    {
        MemoryUpdateResult result;
        lock (_lock)
        {
            var target = _items.GetValueOrDefault(id);
            var normalized = patch.Content is null ? null : MemoryRules.NormalizeContent(patch.Content);
            var rejected = MemoryWriteRules.CheckUpdate(id, target, patch, normalized, _items.Values.ToList(), _options);
            if (rejected is not null)
                return Task.FromResult(rejected);

            var updated = MemoryWriteRules.Apply(target!, patch, normalized, _time.GetUtcNow());
            _items[id] = updated;
            result = new MemoryUpdateResult(MemoryUpdateOutcome.Updated, updated, null);
        }
        SignalChange();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        bool removed;
        lock (_lock)
            removed = _items.Remove(id);
        if (removed)
            SignalChange();
        return Task.FromResult(removed);
    }

    public Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        int count;
        lock (_lock)
        {
            count = _items.Count;
            _items.Clear();
        }
        if (count > 0)
            SignalChange();
        return Task.FromResult(count);
    }

    public Task<int> PurgeExpiredAsync(DateOnly today, int graceDays, CancellationToken ct = default)
    {
        var cutoff = today.AddDays(-graceDays);
        int count;
        lock (_lock)
        {
            var expired = _items.Values.Where(item => item.ExpiresAt is { } expires && expires < cutoff).Select(item => item.Id).ToList();
            foreach (var id in expired)
                _items.Remove(id);
            count = expired.Count;
        }
        if (count > 0)
            SignalChange();
        return Task.FromResult(count);
    }

    /// <summary>排序：分類再 id（與 SQLite 版的 ORDER BY 相同）。呼叫端已持有鎖。</summary>
    private static IReadOnlyList<MemoryItem> Sorted(IEnumerable<MemoryItem> items) =>
        items.OrderBy(item => (int)item.Category).ThenBy(item => item.Id).ToList();

    /// <summary>換成新的權杖，並在鎖外取消舊的（同 <c>ReminderService</c>）。</summary>
    private void SignalChange()
    {
        CancellationTokenSource previous;
        lock (_changeLock)
        {
            previous = _changes;
            _changes = new CancellationTokenSource();
        }
        previous.Cancel();
    }
}
