using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 全域未處理例外寫進 log（M9 9C）。不彈窗（啟動階段由說明視窗處理）。
/// 原則：不吞掉致命例外。AppDomain 的例外照常結束程序，只記錄；TaskScheduler 的未觀察例外標記為已觀察
/// （否則 .NET 在 finalizer 時還會再丟一次）；UI 執行緒的例外記錄後**不**標記為 Handled，讓 Avalonia 照舊處理。
/// 事件本身由 <see cref="Attach"/> 掛上；處理邏輯都是公開方法，測試直接呼叫。
/// </summary>
public sealed class UnhandledExceptionLogger(ILogger<UnhandledExceptionLogger> logger)
{
    private readonly object _gate = new();
    private bool _attached;

    /// <summary>掛上 AppDomain 與 TaskScheduler 的事件。重複呼叫沒有作用。</summary>
    public void Attach()
    {
        lock (_gate)
        {
            if (_attached)
                return;
            _attached = true;
        }
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            logger.LogCritical(ex, "未處理的例外（處理程序{State}）", e.IsTerminating ? "即將結束" : "仍在執行");
        else
            logger.LogCritical("未處理的非例外物件（處理程序{State}）：{Type}", e.IsTerminating ? "即將結束" : "仍在執行", e.ExceptionObject?.GetType().FullName ?? "null");
    }

    public void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        logger.LogError(e.Exception, "背景工作的例外沒有被等待或觀察");
        e.SetObserved();
    }

    /// <summary>UI 執行緒的例外：只記錄，不標記 Handled。</summary>
    public void OnUiThreadException(Exception exception) =>
        logger.LogError(exception, "UI 執行緒發生未處理的例外");
}
