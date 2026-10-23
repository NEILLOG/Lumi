using Lumi.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 啟動時在背景物理刪除到期超過寬限天數（<see cref="MemoryOptions.ExpiredGraceDays"/>）的記憶。
/// 「今天」用本機日期。已過期但還在寬限期內的記憶不刪，只是不再送進對話（<see cref="MemoryRules.IsActive"/>）。
/// 清理失敗只記 log，下次啟動再試。
/// </summary>
public sealed class MemoryCleanupService(IMemoryStore store, MemoryOptions options, TimeProvider time, ILogger<MemoryCleanupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
                var deleted = await store.PurgeExpiredAsync(today, options.ExpiredGraceDays, CancellationToken.None).ConfigureAwait(false);
                if (deleted > 0)
                    logger.LogInformation("清掉 {Count} 則已過期超過寬限期的長期記憶", deleted);
            }
            catch (Exception e)
            {
                // 清理失敗不影響使用：下次啟動再試
                logger.LogWarning(e, "清理過期的長期記憶失敗");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
