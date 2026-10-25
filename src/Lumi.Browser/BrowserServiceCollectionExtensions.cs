using Lumi.Core.Browser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Browser;

public static class BrowserServiceCollectionExtensions
{
    /// <summary>
    /// 瀏覽器自動化（M18）。<see cref="BrowserOptions.ProfileRoot"/> 由呼叫端設定（Lumi.App 指到使用者資料夾下的 <c>browser</c>）。
    /// 瀏覽器工具在 <c>Lumi.Tools</c> 註冊，它們只依賴 Core 的 <see cref="IBrowserSessions"/>。
    /// </summary>
    public static IServiceCollection AddLumiBrowser(this IServiceCollection services, Action<BrowserOptions>? configure = null)
    {
        var options = new BrowserOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IBrowserLocator, BrowserLocator>();
        services.AddSingleton<PlaywrightBrowserSessions>();
        services.Replace(ServiceDescriptor.Singleton<IBrowserSessions>(sp => sp.GetRequiredService<PlaywrightBrowserSessions>()));   // 取代 Core 的 NullBrowserSessions
        services.AddHostedService<BrowserHostedService>();
        return services;
    }
}
