using Lumi.Core.Reminders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 提醒的背景迴圈。啟動時先做一次錯過的檢查；之後等到最近一筆的時間（最多 <see cref="ReminderOptions.MaxWait"/>），
/// 醒來就處理到點的項目。沒有待觸發項目時不排計時器，只等變更訊號（建立、取消、啟停）。
/// 睡眠喚醒（實際經過的時間比預定多出 30 秒以上）時先做錯過的檢查再處理。迴圈內的例外只記 log，5 秒後重試，服務不結束。
/// </summary>
public sealed class ReminderHostedService(ReminderService reminders, TimeProvider time, ILogger<ReminderHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SleepThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var needsStartup = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (needsStartup)
                {
                    await reminders.OnStartupAsync(stoppingToken);
                    needsStartup = false;
                }
                await WaitForNextRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "提醒的背景迴圈發生錯誤，{Seconds} 秒後重試", RetryDelay.TotalSeconds);
                if (!await PauseAsync(RetryDelay, stoppingToken))
                    break;
            }
        }
    }

    private async Task WaitForNextRoundAsync(CancellationToken ct)
    {
        // 先取得變更訊號，再讀等待時間：兩者之間發生的變更會讓下面的等待立刻結束，不會漏掉
        var changed = reminders.ChangeToken;
        var wait = await reminders.GetWaitAsync(ct);
        var started = time.GetUtcNow();

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct, changed);
        try
        {
            if (wait is null)
                await Task.Delay(Timeout.Infinite, wake.Token);   // 沒有待觸發項目：不排計時器
            else
                await Task.Delay(wait.Value, time, wake.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 項目有變更：回到迴圈頂端重新算等待時間
            return;
        }

        if (wait is not { } expected)
            return;

        if (time.GetUtcNow() - started - expected > SleepThreshold)
        {
            logger.LogInformation("偵測到睡眠喚醒，重新檢查錯過的提醒");
            await reminders.OnStartupAsync(ct);
        }
        await reminders.ProcessDueAsync(ct);
    }

    /// <summary>等待；被停止時回 false。</summary>
    private async Task<bool> PauseAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
