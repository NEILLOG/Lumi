using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.Web.Proxy;

/// <summary>啟動時在背景載入 Proxy 設定（下載 PAC）；不等待，工作台照常開啟。</summary>
public sealed class ProxyHostedService(LumiWebProxy proxy, ILogger<ProxyHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = InitializeAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeAsync()
    {
        try
        {
            await proxy.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "載入 Proxy 設定時發生未預期的錯誤");
        }
    }
}
