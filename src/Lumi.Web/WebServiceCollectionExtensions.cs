using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Web;

public static class WebServiceCollectionExtensions
{
    /// <summary>Phase 0 的空殼；註冊內容由 M17 的整合包填入。</summary>
    public static IServiceCollection AddLumiWeb(this IServiceCollection services) => services;
}
