using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Views;

/// <summary>
/// 視窗位置寫檔的節流（M9 9D）：移動或縮放時每 500 ms 最多寫一次，寫的是那段時間內最後的值；
/// 關閉時用 <see cref="FlushAsync"/> 再寫一次，確保最後的狀態一定寫進去。用 <see cref="TimeProvider"/> 計時，測試不用真的等。
/// 同一時間只允許一個寫入（<see cref="_gate"/>），而且寫的永遠是寫入當下最新的值，避免舊值蓋掉新值。
/// </summary>
public sealed class WindowMemoryRecorder : IDisposable
{
    public static readonly TimeSpan DefaultThrottle = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider _time;
    private readonly Func<WindowSettings, Task> _save;
    private readonly ILogger? _logger;
    private readonly TimeSpan _throttle;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private WindowSettings? _pending;
    private WindowSettings? _lastWritten;

    /// <param name="save">實際寫檔（通常是 <c>IModelCatalog.SetWorkbenchWindowAsync</c>）。</param>
    /// <param name="initial">目前已存的值；和它相同的快照不會再寫。</param>
    public WindowMemoryRecorder(TimeProvider time, Func<WindowSettings, Task> save, ILogger? logger = null,
        WindowSettings? initial = null, TimeSpan? throttle = null)
    {
        _time = time;
        _save = save;
        _logger = logger;
        _lastWritten = initial;
        _throttle = throttle ?? DefaultThrottle;
    }

    /// <summary>視窗的位置或大小有變動（呼叫端只傳「正常狀態」的快照）。第一次變動後 500 ms 寫一次。</summary>
    public void Changed(WindowSettings snapshot)
    {
        lock (_lock)
        {
            _pending = snapshot;
            _timer ??= _time.CreateTimer(static state => ((WindowMemoryRecorder)state!).OnTimer(), this, _throttle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 取消等待中的計時，立刻寫入最後的值（<paramref name="final"/> 有值時以它為準）。寫入失敗只記 log，不丟例外。
    /// </summary>
    public Task FlushAsync(WindowSettings? final = null)
    {
        lock (_lock)
        {
            if (final is not null)
                _pending = final;
            _timer?.Dispose();
            _timer = null;
        }
        return DrainAsync();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimer()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        // 所有 await 都 ConfigureAwait(false)：關閉視窗時 UI 執行緒會等這個 Task（有逾時），不能在這裡回到 UI 執行緒。
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            WindowSettings? value;
            lock (_lock)
            {
                value = _pending;
                _pending = null;
            }

            if (value is null || value == _lastWritten)
                return;

            try
            {
                await _save(value).ConfigureAwait(false);
                _lastWritten = value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "儲存工作台視窗位置失敗");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
