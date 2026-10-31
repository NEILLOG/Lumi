using Lumi.Core.Reminders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 排程跑 prompt 的背景服務（M24）：啟動時先把上次沒結束的執行標成中斷、清掉殘留的對話，然後執行佇列（<see cref="ScheduledRunService.RunAsync"/>）。
/// 到點的項目由 <see cref="ReminderHostedService"/> 的迴圈排入佇列。
/// </summary>
public sealed class ScheduledRunHostedService(ScheduledRunService runs, ILogger<ScheduledRunHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runs.RecoverOnStartupAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            // 清理失敗不影響之後的執行
            logger.LogError(e, "啟動時整理排程執行紀錄失敗");
        }

        try
        {
            await runs.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogError(e, "排程執行的背景迴圈結束了");
        }
    }
}
