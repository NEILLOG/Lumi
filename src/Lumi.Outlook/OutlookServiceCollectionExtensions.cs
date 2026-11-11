using Lumi.Core.Outlook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Outlook;

public static class OutlookServiceCollectionExtensions
{
    /// <summary>
    /// classic Outlook 的連線（只有 Windows）。Outlook 工具在 <c>Lumi.Tools</c> 註冊，它們只依賴 Core 的 <c>IOutlookClient</c>；
    /// 其他平台保留 Core 的 <c>NullOutlookClient</c>。
    /// </summary>
    public static IServiceCollection AddLumiOutlook(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
            services.Replace(ServiceDescriptor.Singleton<IOutlookClient, OutlookClient>());   // 取代 Core 的 NullOutlookClient
        return services;
    }
}
