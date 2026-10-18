using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Providers;

public static class ProvidersServiceCollectionExtensions
{
    public static IServiceCollection AddLumiProviders(this IServiceCollection services)
    {
        services.AddOptions<AnthropicProviderOptions>().BindConfiguration(AnthropicProviderOptions.SectionPath);

        // 用 IHttpClientFactory 的 typed client：HttpClientFactory 預設只在 log 裡顯示標頭名稱、
        // 不顯示值（除非加進 RedactLoggedHeadersAllowList），x-api-key、Authorization 不會被加進去，
        // 所以金鑰不會外洩到 log（agent.md 第 10 節）。認證標頭一律設在每次請求上，不放 DefaultRequestHeaders。
        services.AddHttpClient<AnthropicChatProvider>();
        services.AddHttpClient<AnthropicConnectionTester>();

        // 串流回應可能持續很久（本機模型載入、長回答），停止靠 CancellationToken，不靠 HttpClient 的整體逾時
        services.AddHttpClient<OpenAICompatibleChatProvider>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<OpenAICompatibleConnectionTester>(c => c.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<IChatProvider, RoutingChatProvider>();
        services.AddSingleton<IConnectionTester, RoutingConnectionTester>();
        return services;
    }
}
