namespace Lumi.Core.Reminders;

/// <summary>
/// <see cref="IScheduledItemStore"/> 的記憶體版本，給測試與後續的服務測試共用。行為與 SQLite 版一致（篩選、排序、清除規則）。
/// 執行緒安全（一把鎖）。
/// </summary>
public sealed class InMemoryScheduledItemStore : IScheduledItemStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ScheduledItem> _items = new(StringComparer.Ordinal);

    public Task AddAsync(ScheduledItem item, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_items.ContainsKey(item.Id))
                throw new InvalidOperationException($"項目 Id 重複：{item.Id}");
            _items[item.Id] = item;
        }
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(ScheduledItem item, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_items.ContainsKey(item.Id))
                return Task.FromResult(false);
            _items[item.Id] = item;
            return Task.FromResult(true);
        }
    }

    public Task<ScheduledItem?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.GetValueOrDefault(id));
        }
    }

    public Task<IReadOnlyList<ScheduledItem>> ListAsync(ScheduledItemFilter filter, CancellationToken ct = default)
    {
        if (filter.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(filter), "Limit 必須 ≥ 1。");

        lock (_lock)
        {
            IEnumerable<ScheduledItem> query = _items.Values;
            if (filter.Statuses is not null)
                query = query.Where(item => filter.Statuses.Contains(item.Status));
            if (filter.Kinds is not null)
                query = query.Where(item => filter.Kinds.Contains(item.Kind));

            query = query.OrderBy(SortKey).ThenBy(item => item.Id, StringComparer.Ordinal);
            if (filter.Limit is { } limit)
                query = query.Take(limit);
            return Task.FromResult<IReadOnlyList<ScheduledItem>>(query.ToList());
        }
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.Values.Count(item => item.Status == ScheduledStatus.Pending));
        }
    }

    public Task<IReadOnlyList<ScheduledItem>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ScheduledItem> due = _items.Values
                .Where(item => IsActivePending(item) && item.NextFireAt <= now)
                .OrderBy(item => item.NextFireAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(due);
        }
    }

    public Task<DateTimeOffset?> GetNextFireAtAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var next = _items.Values
                .Where(IsActivePending)
                .Select(item => item.NextFireAt)
                .Min();
            return Task.FromResult(next);
        }
    }

    public Task<int> PurgeFinishedAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var doomed = _items.Values
                .Where(item => IsFinished(item.Status) && (item.LastFiredAt ?? item.NextFireAt ?? item.CreatedAt) < olderThan)
                .Select(item => item.Id)
                .ToList();
            foreach (var id in doomed)
                _items.Remove(id);
            return Task.FromResult(doomed.Count);
        }
    }

    private static DateTimeOffset SortKey(ScheduledItem item) => item.NextFireAt ?? item.CreatedAt;

    private static bool IsActivePending(ScheduledItem item) =>
        item.Enabled && item.Status == ScheduledStatus.Pending && item.NextFireAt is not null;

    private static bool IsFinished(ScheduledStatus status) =>
        status is ScheduledStatus.Completed or ScheduledStatus.Cancelled or ScheduledStatus.Missed;
}
