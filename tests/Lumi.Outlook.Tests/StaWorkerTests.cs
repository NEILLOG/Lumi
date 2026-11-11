using Lumi.Core.Outlook;

namespace Lumi.Outlook.Tests;

public class StaWorkerTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private const string NotResponding = "Outlook 沒有回應，請看一下 Outlook 是不是跳出了視窗。";

    [Fact]
    public async Task Runs_in_order()
    {
        using var worker = new StaWorker(useSta: OperatingSystem.IsWindows());
        var order = new List<int>();

        var first = await worker.RunAsync(() => { order.Add(1); return 1; }, TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = await worker.RunAsync(() => { order.Add(2); return 2; }, TimeSpan.FromSeconds(5), CancellationToken.None);
        var third = await worker.RunAsync(() => { order.Add(3); return 3; }, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, order);
        Assert.Equal(new[] { 1, 2, 3 }, new[] { first, second, third });
    }

    [Fact]
    public async Task Timeout_returns_error_and_marks_stuck()
    {
        using var release = new ManualResetEventSlim(false);
        using var worker = new StaWorker(useSta: OperatingSystem.IsWindows());
        try
        {
            var ex = await Assert.ThrowsAsync<OutlookException>(() =>
                worker.RunAsync(() => { release.Wait(); return 1; }, ShortTimeout, CancellationToken.None));
            Assert.Equal(NotResponding, ex.Message);

            // 逾時的呼叫還沒返回：worker 處於卡住狀態，新的呼叫立刻失敗
            var stuck = await Assert.ThrowsAsync<OutlookException>(() =>
                worker.RunAsync(() => 2, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(NotResponding, stuck.Message);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Stuck_worker_fails_fast_until_call_returns()
    {
        using var release = new ManualResetEventSlim(false);
        using var worker = new StaWorker(useSta: OperatingSystem.IsWindows());
        try
        {
            await Assert.ThrowsAsync<OutlookException>(() =>
                worker.RunAsync(() => { release.Wait(); return 1; }, ShortTimeout, CancellationToken.None));

            // 卡住期間：立刻失敗，不排隊（用很長的逾時也不會等）
            var started = DateTime.UtcNow;
            await Assert.ThrowsAsync<OutlookException>(() =>
                worker.RunAsync(() => 2, TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));

            // 卡住的呼叫返回後，下一個呼叫要成功
            release.Set();
            var recovered = await WaitForSuccessAsync(worker, () => 3);
            Assert.Equal(3, recovered);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Cancel_before_start_throws()
    {
        using var worker = new StaWorker(useSta: OperatingSystem.IsWindows());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            worker.RunAsync(() => { ran = true; return 1; }, TimeSpan.FromSeconds(5), cts.Token));

        Assert.False(ran);
    }

    [Fact]
    public async Task Work_exception_is_propagated()
    {
        using var worker = new StaWorker(useSta: OperatingSystem.IsWindows());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.RunAsync<int>(() => throw new InvalidOperationException("測試用的例外"), TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal("測試用的例外", ex.Message);
    }

    /// <summary>卡住解除是在背景執行緒完成的，給它一點時間。</summary>
    private static async Task<T> WaitForSuccessAsync<T>(StaWorker worker, Func<T> work)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                return await worker.RunAsync(work, TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (OutlookException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }
}
