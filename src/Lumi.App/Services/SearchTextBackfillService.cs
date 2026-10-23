using Lumi.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 啟動時在背景補上舊訊息的搜尋文字（migration 4 之前的訊息）。只跑一次（完成後有旗標），不阻擋啟動；
/// 關閉程式時取消，沒做完的下次啟動接著做。
/// </summary>
public sealed class SearchTextBackfillService(SearchTextBackfill backfill, ILogger<SearchTextBackfillService> logger) : IHostedService
{
    private readonly CancellationTokenSource _stop = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await backfill.RunAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("補寫搜尋文字已中斷，下次啟動接著做");
            }
            catch (Exception e)
            {
                // 失敗不影響使用：搜尋只是對舊訊息找不到，下次啟動再試
                logger.LogWarning(e, "補寫搜尋文字失敗");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        return Task.CompletedTask;
    }
}
