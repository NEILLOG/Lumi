using Lumi.Core.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tools.Tests.Web;

/// <summary>網路工具的相依服務（M17）由 Lumi.Web 提供；測試組 <c>AddLumiTools</c> 時用假的取代。</summary>
internal static class WebTestServices
{
    public static IServiceCollection AddFakeWebServices(this IServiceCollection services)
    {
        services.AddSingleton<IWebClient>(new FakeWebClient());
        services.AddSingleton<IWebContentExtractor>(new FakeWebContentExtractor());
        services.AddSingleton<IFeedReader>(new FakeFeedReader());
        services.AddSingleton<INetworkDiagnostics>(new FakeNetworkDiagnostics());
        return services;
    }
}
