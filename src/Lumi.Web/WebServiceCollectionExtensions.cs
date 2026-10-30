using System.Net;
using Lumi.Core.Updates;
using Lumi.Core.Usage;
using Lumi.Core.Web;
using Lumi.Web.Content;
using Lumi.Web.Diagnostics;
using LumiWebClient = Lumi.Web.Http.WebClient;
using Lumi.Web.Pricing;
using Lumi.Web.Proxy;
using Lumi.Web.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Web;

public static class WebServiceCollectionExtensions
{
    /// <summary>
    /// 網路基礎（M17）：Proxy、網路工具用的 HTTP 用戶端與內容轉換。必須在其他會用 <c>AddHttpClient</c> 的註冊之前呼叫，
    /// 這樣預設的 HttpClient 設定（走 Lumi 的 Proxy）才會套用到 AI 連線、語音與 MCP。
    /// </summary>
    public static IServiceCollection AddLumiWeb(this IServiceCollection services)
    {
        services.AddSingleton<LumiWebProxy>();
        services.AddSingleton<IWebProxy>(sp => sp.GetRequiredService<LumiWebProxy>());
        services.AddSingleton<INetworkProxyInfo>(sp => sp.GetRequiredService<LumiWebProxy>());
        services.AddSingleton<IntegratedAuthCredentials>();
        services.AddHostedService<ProxyHostedService>();

        // Lumi 所有的 HttpClient（AI 連線、語音、MCP、網路工具）都經過同一個 Proxy（M17 D2）；GetProxy 每次請求都問，設定改了立即生效
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
        {
            Proxy = sp.GetRequiredService<IWebProxy>(),
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }));

        // 網路工具：自己處理轉址、不存 Cookie、自動解壓縮；只對使用者列出的網站送 Windows 身分（D7、D8）。逾時由 WebClient 自己管
        services.AddHttpClient<IWebClient, LumiWebClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                Proxy = sp.GetRequiredService<IWebProxy>(),
                UseProxy = true,
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                Credentials = sp.GetRequiredService<IntegratedAuthCredentials>(),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.AddSingleton<IWebContentExtractor, HtmlContentExtractor>();
        services.AddSingleton<IFeedReader, FeedReader>();
        services.AddSingleton<INetworkDiagnostics, NetworkDiagnostics>();
        services.AddSingleton<INetworkTester, NetworkTester>();
        // 檢查新版：取代 Core 預設的 NullUpdateChecker，讀 GitHub Releases（走同一個 Proxy）
        services.Replace(ServiceDescriptor.Singleton<IUpdateChecker, GitHubReleaseUpdateChecker>());
        return services;
    }

    /// <summary>
    /// 模型價目表（M23）：內建 LiteLLM 快照，使用者資料夾的版本優先，「更新價目表」經 <see cref="IWebClient"/>（走 Proxy）下載。
    /// <paramref name="dataDirectory"/> 由呼叫端傳入（這個專案不引用 Lumi.Infrastructure）。
    /// </summary>
    public static IServiceCollection AddLumiPriceTable(this IServiceCollection services, string dataDirectory)
    {
        services.AddSingleton<IPriceTableSource>(sp => new PriceTableSource(
            sp.GetRequiredService<IWebClient>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PriceTableSource>>(),
            dataDirectory));
        return services;
    }
}
