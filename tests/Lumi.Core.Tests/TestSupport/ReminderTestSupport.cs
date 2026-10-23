using System.Globalization;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.TestSupport;

/// <summary>手動推進的時鐘：提醒服務只讀 <see cref="TimeProvider.GetUtcNow"/>，測試用 <see cref="SetNow"/> 跳時間（含睡眠後的跳躍）。</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void SetNow(DateTimeOffset now) => _now = now;
}

/// <summary>記下每一則通知。<see cref="FailuresRemaining"/> 讓前幾次顯示丟例外；<see cref="OnShowing"/> 在記錄前執行（檢查當下的資料庫狀態）。</summary>
internal sealed class RecordingNotificationSink : INotificationSink
{
    private readonly List<AppNotification> _shown = [];

    public IReadOnlyList<AppNotification> Shown
    {
        get
        {
            lock (_shown)
                return _shown.ToList();
        }
    }

    public int FailuresRemaining { get; set; }

    public Func<AppNotification, Task>? OnShowing { get; set; }

    public async Task ShowAsync(AppNotification notification, CancellationToken ct)
    {
        if (OnShowing is not null)
            await OnShowing(notification);
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new InvalidOperationException("模擬顯示失敗");
        }
        lock (_shown)
            _shown.Add(notification);
    }
}

/// <summary>前 <c>collisions</c> 次寫入會先放一筆同 Id 的既有項目，讓寫入撞到主鍵（模擬隨機 Id 碰撞）。其他方法直接委派。</summary>
internal sealed class CollidingScheduledItemStore(IScheduledItemStore inner, int collisions) : IScheduledItemStore
{
    private int _collisionsLeft = collisions;

    public int AddAttempts { get; private set; }

    public async Task AddAsync(ScheduledItem item, CancellationToken ct = default)
    {
        AddAttempts++;
        if (_collisionsLeft > 0)
        {
            _collisionsLeft--;
            await inner.AddAsync(item with { Title = "既有項目" }, ct);
        }
        await inner.AddAsync(item, ct);
    }

    public Task<bool> UpdateAsync(ScheduledItem item, CancellationToken ct = default) => inner.UpdateAsync(item, ct);

    public Task<ScheduledItem?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);

    public Task<IReadOnlyList<ScheduledItem>> ListAsync(ScheduledItemFilter filter, CancellationToken ct = default) =>
        inner.ListAsync(filter, ct);

    public Task<int> CountPendingAsync(CancellationToken ct = default) => inner.CountPendingAsync(ct);

    public Task<IReadOnlyList<ScheduledItem>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        inner.GetDueAsync(now, ct);

    public Task<DateTimeOffset?> GetNextFireAtAsync(CancellationToken ct = default) => inner.GetNextFireAtAsync(ct);

    public Task<int> PurgeFinishedAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
        inner.PurgeFinishedAsync(olderThan, ct);
}

/// <summary>組好的提醒服務（記憶體儲存、手動時鐘、記錄用的通知）。</summary>
internal sealed class ReminderHarness
{
    public ReminderHarness(ReminderOptions? options = null, IScheduledItemStore? store = null, DateTimeOffset? now = null)
    {
        Clock = new ManualClock(now ?? ReminderTestData.Start);
        Options = options ?? new ReminderOptions();
        Store = store ?? new InMemoryScheduledItemStore();
        Sink = new RecordingNotificationSink();
        Service = new ReminderService(Store, Sink, Clock, Options, NullLogger<ReminderService>.Instance);
    }

    public ManualClock Clock { get; }

    public ReminderOptions Options { get; }

    public IScheduledItemStore Store { get; }

    public RecordingNotificationSink Sink { get; }

    public ReminderService Service { get; }
}

internal static class ReminderTestData
{
    /// <summary>測試的「現在」：2026-10-23 08:00（台北）。</summary>
    public static readonly DateTimeOffset Start = T("2026-10-23T08:00+08:00");

    public static DateTimeOffset T(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    public static NewReminder OneTime(string at, string title = "吃藥", string message = "飯後吃藥。", string? zone = "Asia/Taipei") =>
        new(title, message, new OneTimeTrigger(T(at)), zone, ScheduledKind.Reminder, ScheduledCreator.User);

    public static NewReminder Daily(string time, string title = "喝水", string? zone = "Asia/Taipei") =>
        new(title, "記得喝水。", new RecurrenceTrigger(RecurrenceKind.Daily, TimeOnly.Parse(time, CultureInfo.InvariantCulture), null, null),
            zone, ScheduledKind.Schedule, ScheduledCreator.User);
}
