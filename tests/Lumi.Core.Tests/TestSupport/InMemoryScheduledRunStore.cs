using Lumi.Core.Reminders;

namespace Lumi.Core.Tests.TestSupport;

/// <summary><see cref="IScheduledRunStore"/> 的記憶體版（測試用）。</summary>
internal sealed class InMemoryScheduledRunStore : IScheduledRunStore
{
    private readonly List<ScheduledRun> _runs = [];
    private readonly object _lock = new();

    public Task AddAsync(ScheduledRun run, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_runs.Any(r => r.Id == run.Id))
                throw new InvalidOperationException("紀錄 Id 重複。");
            _runs.Add(run);
        }
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(ScheduledRun run, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var index = _runs.FindIndex(r => r.Id == run.Id);
            if (index < 0)
                return Task.FromResult(false);
            _runs[index] = run;
            return Task.FromResult(true);
        }
    }

    public Task<ScheduledRun?> GetAsync(string runId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_runs.FirstOrDefault(r => r.Id == runId));
    }

    public Task<ScheduledRun?> GetByConversationAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_runs.FirstOrDefault(r => r.ConversationId == conversationId));
    }

    public Task<IReadOnlyList<ScheduledRunGroup>> ListGroupedAsync(int perScheduleLimit, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ScheduledRunGroup> groups = _runs
                .GroupBy(r => r.ScheduleId)
                .Select(g =>
                {
                    var ordered = g.OrderByDescending(r => r.PlannedAt).Take(perScheduleLimit).ToList();
                    return new ScheduledRunGroup(g.Key, ordered[0].ScheduleTitle, ordered);
                })
                .OrderByDescending(g => g.Runs[0].PlannedAt)
                .ToList();
            return Task.FromResult(groups);
        }
    }

    public Task<IReadOnlyList<ScheduledRun>> ListForScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<ScheduledRun>>(
                _runs.Where(r => r.ScheduleId == scheduleId).OrderByDescending(r => r.PlannedAt).ToList());
    }

    public Task<int> CountUnreadAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_runs.Count(r => r.IsUnread));
    }

    public Task MarkReadAsync(string runId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var index = _runs.FindIndex(r => r.Id == runId);
            if (index >= 0 && _runs[index].ReadAt is null)
                _runs[index] = _runs[index] with { ReadAt = at };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledRun>> ListBeyondAsync(string scheduleId, int keep, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<ScheduledRun>>(_runs
                .Where(r => r.ScheduleId == scheduleId)
                .OrderByDescending(r => r.PlannedAt)
                .Skip(keep)
                .Where(r => !r.IsActive)
                .ToList());
    }

    public Task<bool> DeleteAsync(string runId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_runs.RemoveAll(r => r.Id == runId) > 0);
    }

    public Task<int> MarkActiveInterruptedAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var count = 0;
            for (var i = 0; i < _runs.Count; i++)
            {
                if (!_runs[i].IsActive)
                    continue;
                _runs[i] = _runs[i] with { Status = ScheduledRunStatus.Interrupted, FinishedAt = at };
                count++;
            }
            return Task.FromResult(count);
        }
    }

    public Task<IReadOnlyList<string>> ListOrphanRunConversationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
