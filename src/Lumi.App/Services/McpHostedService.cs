using Lumi.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 啟動時在背景連上所有已啟用的 MCP 伺服器（M16 D9）；不等待連線完成，工作台照常開啟。
/// 新對話第一次送出時才會等還沒連好的伺服器（<c>ToolCatalog</c>）。程式結束時關閉所有連線，stdio 程序跟著結束。
/// </summary>
public sealed class McpHostedService(McpServerManager manager, ILogger<McpHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await manager.StartAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // 單一伺服器失敗已經由管理員處理（狀態與通知）；這裡只攔下意外，不影響其他啟動項目
                logger.LogWarning(e, "啟動 MCP 伺服器時發生未預期的錯誤");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await manager.DisposeAsync().ConfigureAwait(false);
}
