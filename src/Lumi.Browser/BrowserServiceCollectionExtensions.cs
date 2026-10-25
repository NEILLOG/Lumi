using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Browser;

public static class BrowserServiceCollectionExtensions
{
    /// <summary>Phase 0 的空殼；註冊內容由 M18 的整合包填入。</summary>
    public static IServiceCollection AddLumiBrowser(this IServiceCollection services) => services;
}
