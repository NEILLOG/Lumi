using System.Collections.Concurrent;
using Lumi.Core.Outlook;

namespace Lumi.Outlook;

/// <summary>
/// 所有 COM 呼叫都經過這裡：一條背景執行緒（Windows 上是 STA），一次只做一個呼叫。
/// 呼叫超過逾時還沒做完：呼叫端拿到逾時錯誤，worker 標記為「卡住」（通常是 Outlook 跳出了對話框），
/// 直到那個呼叫真的返回才解除；卡住期間新的呼叫立刻失敗，不排隊。卡住的呼叫無法中止。
/// </summary>
internal sealed class StaWorker : IDisposable
{
    // 和 OutlookErrors 的同名訊息一樣；合併後可以改用 OutlookErrors 的常數
    private const string NotResponding = "Outlook 沒有回應，請看一下 Outlook 是不是跳出了視窗。";

    private readonly BlockingCollection<Job> _queue = new();
    private readonly object _gate = new();
    private readonly Thread _thread;

    // 以下兩個欄位都由 _gate 保護
    private Job? _stuckJob;
    private bool _disposed;

    /// <param name="useSta">要求 STA（只有 Windows 會真的設定）。</param>
    public StaWorker(bool useSta)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Lumi.Outlook.Worker" };
        if (useSta && OperatingSystem.IsWindows())
            _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// 排隊執行 work，等它完成或逾時。
    /// 逾時從排隊那一刻開始計算，不因呼叫端取消而停止（取消後 work 仍在跑，逾時照樣會讓 worker 卡住）。
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var job = new Job<T>(work);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stuckJob is not null)
                throw new OutlookException(NotResponding);
            _queue.Add(job);
        }

        _ = Task.Delay(timeout).ContinueWith(_ => MarkStuckIfUnfinished(job), TaskScheduler.Default);

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => cancelled.TrySetResult());

        var winner = await Task.WhenAny(job.Result.Task, job.TimedOut.Task, cancelled.Task).ConfigureAwait(false);

        // 結果優先：逾時和完成幾乎同時發生時，以完成的結果為準
        if (job.Result.Task.IsCompleted)
            return await job.Result.Task.ConfigureAwait(false);

        if (winner == job.TimedOut.Task)
            throw new OutlookException(NotResponding);

        // 等待中被取消：work 可能還在跑，結果之後丟掉
        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        // 已排隊的呼叫會照常做完；執行緒是背景執行緒，不會擋住程式結束
        _queue.CompleteAdding();
    }

    private void Loop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            job.Execute();
            lock (_gate)
            {
                job.Finished = true;
                if (ReferenceEquals(_stuckJob, job))
                    _stuckJob = null;
            }
        }
    }

    private void MarkStuckIfUnfinished(Job job)
    {
        lock (_gate)
        {
            if (job.Finished)
                return;
            _stuckJob = job;
        }
        job.TimedOut.TrySetResult();
    }

    private abstract class Job
    {
        /// <summary>由 _gate 保護。</summary>
        public bool Finished;

        public readonly TaskCompletionSource TimedOut = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public abstract void Execute();
    }

    private sealed class Job<T>(Func<T> work) : Job
    {
        public readonly TaskCompletionSource<T> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute()
        {
            try
            {
                Result.TrySetResult(work());
            }
            catch (Exception ex)
            {
                // 例外原樣交給呼叫端
                Result.TrySetException(ex);
            }
        }
    }
}
