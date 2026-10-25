using Lumi.Core.Browser;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.Browser;

/// <summary>
/// 程式結束時關閉整個瀏覽器（Playwright 的驅動程序與 Edge／Chrome 程序跟著結束）。啟動時什麼都不做：
/// 瀏覽器是第一次需要時才啟動（M18 D10）。
/// </summary>
public sealed class BrowserHostedService(IBrowserSessions sessions, ILogger<BrowserHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sessions.CloseAllAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "關閉瀏覽器時發生錯誤");
        }
    }
}
