using Lumi.Core.Context;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>啟動時在背景清掉沒有被任何訊息引用的附件檔案（那時候不會有送出進行中，不會誤刪剛存好、訊息還沒存的圖片）。</summary>
public sealed class AttachmentCleanupService(AttachmentCleaner cleaner, ILogger<AttachmentCleanupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await cleaner.CleanAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // 清理失敗不影響使用：下次啟動再試
                logger.LogWarning(e, "清理附件檔案失敗");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
