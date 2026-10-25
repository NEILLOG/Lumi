using Lumi.Core.Browser;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tools.Tests.Browser;

/// <summary>瀏覽器工具的相依服務（M18）由 Lumi.Browser 提供；測試組 <c>AddLumiTools</c> 時用假的取代。</summary>
internal static class BrowserTestServices
{
    public static IServiceCollection AddFakeBrowserServices(this IServiceCollection services)
    {
        services.AddSingleton<IBrowserSessions>(new FakeBrowserSessions());
        return services;
    }
}
