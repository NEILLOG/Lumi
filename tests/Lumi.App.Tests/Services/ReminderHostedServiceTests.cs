using System.Globalization;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Services;

/// <summary>
/// 提醒背景迴圈：真的 <see cref="ReminderService"/> ＋ 記憶體儲存 ＋ 手動時間。
/// 迴圈在背景執行緒上跑，等待條件時用真實時間輪詢（間隔很短），時間本身一律由 <see cref="ManualTimeProvider"/> 推進。
/// </summary>
public sealed class ReminderHostedServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T08:00+08:00", CultureInfo.InvariantCulture);

    private sealed class Harness
    {
        public Harness(IScheduledItemStore? store = null)
        {
            Time = new ManualTimeProvider(Start);
            Store = store ?? new InMemoryScheduledItemStore();
            Sink = new RecordingSink();
            Reminders = new ReminderService(Store, Sink, Time, new ReminderOptions(), NullLogger<ReminderService>.Instance);
            Host = new ReminderHostedService(Reminders, Time, NullLogger<ReminderHostedService>.Instance);
        }

        public ManualTimeProvider Time { get; }

        public IScheduledItemStore Store { get; }

        public RecordingSink Sink { get; }

        public ReminderService Reminders { get; }

        public ReminderHostedService Host { get; }

        public async Task StopAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Host.StopAsync(stop.Token);
        }
    }

    /// <summary>記下通知。</summary>
    private sealed class RecordingSink : INotificationSink
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

        public Task ShowAsync(AppNotification notification, CancellationToken ct)
        {
            lock (_shown)
                _shown.Add(notification);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 委派給記憶體儲存；計算等待時間（迴圈每一輪都會做）的前 <c>failures</c> 次丟例外，模擬暫時的資料庫錯誤。
    /// <see cref="WaitCalls"/> 讓測試知道迴圈已經走到等待的那一步。
    /// </summary>
    private sealed class FlakyWaitStore(IScheduledItemStore inner, int failures) : IScheduledItemStore
    {
        private int _failuresLeft = failures;

        public int WaitCalls { get; private set; }

        public Task AddAsync(ScheduledItem item, CancellationToken ct = default) => inner.AddAsync(item, ct);

        public Task<bool> UpdateAsync(ScheduledItem item, CancellationToken ct = default) => inner.UpdateAsync(item, ct);

        public Task<ScheduledItem?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public Task<IReadOnlyList<ScheduledItem>> ListAsync(ScheduledItemFilter filter, CancellationToken ct = default) =>
            inner.ListAsync(filter, ct);

        public Task<int> CountPendingAsync(CancellationToken ct = default) => inner.CountPendingAsync(ct);

        public Task<IReadOnlyList<ScheduledItem>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
            inner.GetDueAsync(now, ct);

        public Task<DateTimeOffset?> GetNextFireAtAsync(CancellationToken ct = default)
        {
            WaitCalls++;
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new InvalidOperationException("模擬資料庫暫時失敗");
            }
            return inner.GetNextFireAtAsync(ct);
        }

        public Task<int> PurgeFinishedAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
            inner.PurgeFinishedAsync(olderThan, ct);

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

        public Task<int> DeleteFinishedAsync(CancellationToken ct = default) => inner.DeleteFinishedAsync(ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等待逾時：" + because);
            await Task.Delay(10);
        }
    }

    private static NewReminder OneTimeAt(DateTimeOffset at, string title) => new(
        title, "時間到了。", new OneTimeTrigger(at), "Asia/Taipei", ScheduledKind.Reminder, ScheduledCreator.User);

    [Fact]
    public async Task Start_RunsStartupCheck_FirstMarkingMissedItems()
    {
        var h = new Harness();
        var item = await h.Reminders.CreateReminderAsync(OneTimeAt(Start.AddHours(1), "吃藥"), default);
        h.Time.Advance(TimeSpan.FromHours(2));   // 程式沒開的兩個小時

        await h.Host.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => h.Sink.Shown.Count > 0, "錯過的彙總通知");

            Assert.Equal(ScheduledStatus.Missed, (await h.Store.GetAsync(item.Id))!.Status);
            Assert.Equal(NotificationKind.MissedSummary, h.Sink.Shown.Single().Kind);
        }
        finally
        {
            await h.StopAsync();
        }
    }

    [Fact]
    public async Task NoPendingItems_DoesNotScheduleTimer()
    {
        var store = new FlakyWaitStore(new InMemoryScheduledItemStore(), failures: 0);
        var h = new Harness(store);

        await h.Host.StartAsync(default);
        try
        {
            // 等迴圈走到「計算等待時間」，確認那時候已經沒有項目；這時候不能有任何計時器
            await WaitUntilAsync(() => store.WaitCalls >= 1, "迴圈計算等待時間");
            Assert.Equal(0, h.Time.ActiveTimerCount);
        }
        finally
        {
            await h.StopAsync();
        }
    }

    [Fact]
    public async Task ChangeSignal_WakesTheLoop_AndTheItemFiresOnTime()
    {
        var store = new FlakyWaitStore(new InMemoryScheduledItemStore(), failures: 0);
        var h = new Harness(store);
        await h.Host.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => store.WaitCalls >= 1, "迴圈進入等待");
            Assert.Equal(0, h.Time.ActiveTimerCount);

            // 建立提醒：變更訊號喚醒迴圈，迴圈改排 30 秒後的計時器
            await h.Reminders.CreateReminderAsync(OneTimeAt(Start.AddSeconds(30), "開會"), default);
            await WaitUntilAsync(() => h.Time.ActiveTimerCount == 1, "變更後排上計時器");

            h.Time.Advance(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => h.Sink.Shown.Count == 1, "到點的提醒");
            Assert.Equal("開會", h.Sink.Shown.Single().Title);
        }
        finally
        {
            await h.StopAsync();
        }
    }

    [Fact]
    public async Task ExceptionInLoop_IsLoggedAndRetried_ServiceKeepsRunning()
    {
        var store = new FlakyWaitStore(new InMemoryScheduledItemStore(), failures: 1);
        var h = new Harness(store);
        await h.Host.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => store.WaitCalls >= 1, "第一次失敗");
            // 失敗後等 5 秒才重試（這個等待是唯一的計時器）
            await WaitUntilAsync(() => h.Time.ActiveTimerCount == 1, "失敗後的重試等待");
            Assert.False(h.Host.ExecuteTask!.IsCompleted);

            h.Time.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => store.WaitCalls >= 2, "重試");
            Assert.False(h.Host.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await h.StopAsync();
        }
    }

    [Fact]
    public async Task Stop_EndsTheLoopCleanly()
    {
        var store = new FlakyWaitStore(new InMemoryScheduledItemStore(), failures: 0);
        var h = new Harness(store);
        await h.Host.StartAsync(default);
        await WaitUntilAsync(() => store.WaitCalls >= 1, "迴圈進入等待");

        await h.StopAsync();

        Assert.True(h.Host.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SleepWake_RunsStartupCheck_BeforeProcessingDueItems()
    {
        var h = new Harness();
        var item = await h.Reminders.CreateReminderAsync(OneTimeAt(Start.AddMinutes(10), "會議"), default);

        await h.Host.StartAsync(default);
        try
        {
            // 等待最多 60 秒（迴圈的上限）
            await WaitUntilAsync(() => h.Time.ActiveTimerCount == 1, "排上計時器");

            // 模擬睡眠：計時器到期時，實際經過了三個小時
            h.Time.Advance(TimeSpan.FromHours(3));

            await WaitUntilAsync(() => h.Sink.Shown.Count > 0, "睡眠喚醒後的錯過通知");
            Assert.Equal(ScheduledStatus.Missed, (await h.Store.GetAsync(item.Id))!.Status);
            Assert.Equal(NotificationKind.MissedSummary, h.Sink.Shown.Single().Kind);
        }
        finally
        {
            await h.StopAsync();
        }
    }
}
