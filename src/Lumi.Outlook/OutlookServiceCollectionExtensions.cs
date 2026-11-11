using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Outlook;

public static class OutlookServiceCollectionExtensions
{
    /// <summary>
    /// classic Outlook 的連線（只有 Windows）。Outlook 工具在 <c>Lumi.Tools</c> 註冊，它們只依賴 Core 的 <c>IOutlookClient</c>；
    /// 其他平台保留 Core 的 <c>NullOutlookClient</c>。
    /// </summary>
    public static IServiceCollection AddLumiOutlook(this IServiceCollection services)
    {
        // TODO(30B)：Windows 上把 IOutlookClient 換成 OutlookClient
        return services;
    }
}
