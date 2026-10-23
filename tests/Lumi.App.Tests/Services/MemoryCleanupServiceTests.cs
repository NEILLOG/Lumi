using System.Globalization;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Services;

/// <summary>M14 14A：啟動時的記憶清理（與 <see cref="AttachmentCleanupService"/> 同樣是背景跑一次）。</summary>
public sealed class MemoryCleanupServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T08:00:00+00:00", CultureInfo.InvariantCulture);

    private static readonly TimeZoneInfo PlusEight = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private static NewMemory New(string content, DateOnly? expires = null) =>
        new(content, MemoryCategory.Profile, MemorySource.Manual, null, expires);

    /// <summary>等背景的清理跑完（用真實時間輪詢，間隔很短）。</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("背景清理沒有在時限內完成。");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Startup_PurgesOnlyItemsBeyondTheGraceDays()
    {
        var store = new InMemoryMemoryStore(new MemoryOptions(), new ManualTimeProvider(Start));
        var beyond = await store.AddAsync(New("寬限期外", new DateOnly(2026, 9, 22)));
        var onBoundary = await store.AddAsync(New("剛好在界線", new DateOnly(2026, 9, 23)));
        var expiringToday = await store.AddAsync(New("今天到期", new DateOnly(2026, 10, 23)));
        var permanent = await store.AddAsync(New("永久"));
        var service = new MemoryCleanupService(store, new MemoryOptions { ExpiredGraceDays = 30 }, new ManualTimeProvider(Start), NullLogger<MemoryCleanupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        await WaitUntilAsync(async () => (await store.ListAsync()).Count == 3);
        var remaining = (await store.ListAsync()).Select(i => i.Id).ToList();
        Assert.DoesNotContain(beyond.Item!.Id, remaining);
        Assert.Contains(onBoundary.Item!.Id, remaining);
        Assert.Contains(expiringToday.Item!.Id, remaining);
        Assert.Contains(permanent.Item!.Id, remaining);
    }

    [Fact]
    public async Task Startup_UsesTheLocalDate_NotUtc()
    {
        // UTC 是 10-23 20:00，本機（UTC+8）已經是 10-24 04:00：寬限界線是 09-24，所以 09-23 到期的要刪
        var localClock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-23T20:00:00+00:00", CultureInfo.InvariantCulture), PlusEight);
        var store = new InMemoryMemoryStore(new MemoryOptions(), localClock);
        await store.AddAsync(New("09-23 到期", new DateOnly(2026, 9, 23)));
        await store.AddAsync(New("09-24 到期", new DateOnly(2026, 9, 24)));
        var service = new MemoryCleanupService(store, new MemoryOptions { ExpiredGraceDays = 30 }, localClock, NullLogger<MemoryCleanupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        await WaitUntilAsync(async () => (await store.ListAsync()).Count == 1);
        Assert.Equal("09-24 到期", (await store.ListAsync()).Single().Content);
    }

    [Fact]
    public async Task Startup_WhenThePurgeFails_DoesNotThrow_AndTheStoreIsStillUsable()
    {
        var store = new FailingPurgeStore();
        var service = new MemoryCleanupService(store, new MemoryOptions(), new ManualTimeProvider(Start), NullLogger<MemoryCleanupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(store.Attempts == 1));
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>只有清理會被呼叫的部分有實作，其他成員不應該被清理流程碰到。</summary>
    private sealed class FailingPurgeStore : IMemoryStore
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<int> PurgeExpiredAsync(DateOnly today, int graceDays, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("模擬的資料庫錯誤");
        }

        public CancellationToken ChangeToken => CancellationToken.None;

        public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryItem>> ListActiveAsync(DateOnly today, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MemoryItem?> GetAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();

        public MemoryItem? TryGet(int id) => throw new NotSupportedException();

        public Task<MemoryAddResult> AddAsync(NewMemory memory, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MemoryUpdateResult> UpdateAsync(int id, MemoryPatch patch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> DeleteAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
