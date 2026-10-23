namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 手動推進、而且連計時器（<c>Task.Delay(…, TimeProvider)</c>）也跟著假時間走：<see cref="Advance"/> 之後到期的計時器會立刻觸發。
/// <see cref="FakeTimeProvider"/> 只有時鐘，計時器仍是真的，所以需要等「30 分鐘」的測試要用這個。
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly TimeZoneInfo _zone;
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null, TimeZoneInfo? zone = null)
    {
        _now = start ?? new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => _zone;

    /// <summary>直接設定時間（不觸發計時器），用來測午夜等邊界。</summary>
    public void Set(DateTimeOffset now)
    {
        lock (_lock)
            _now = now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt is { } at && at <= _now)];
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; private set; }

        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                owner._timers.Remove(this);
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    return true;
                }
                DueAt = owner._now + dueTime;
                owner._timers.Add(this);
                return true;
            }
        }

        public void Fire()
        {
            lock (owner._lock)
            {
                if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                {
                    DueAt = null;
                    owner._timers.Remove(this);
                }
                else
                {
                    DueAt = owner._now + _period;
                }
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._timers.Remove(this);
                DueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
