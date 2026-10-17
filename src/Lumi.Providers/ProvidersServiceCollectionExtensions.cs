using Lumi.Core.Chat;
using Lumi.Providers.Anthropic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lumi.Providers;

public static class ProvidersServiceCollectionExtensions
{
    public static IServiceCollection AddLumiProviders(this IServiceCollection services)
    {
        services
            .AddOptions<AnthropicDevOptions>()
            .BindConfiguration(AnthropicDevOptions.SectionPath)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // 用 IHttpClientFactory 的 typed client：HttpClientFactory 預設只在 log 裡顯示標頭名稱、
        // 不顯示值（除非加進 RedactLoggedHeadersAllowList），x-api-key、Authorization 不會被加進去，
        // 所以金鑰不會外洩到 log（agent.md 第 10 節）。
        services.AddHttpClient<AnthropicChatProvider>();
        services.AddSingleton<IChatProvider>(sp => sp.GetRequiredService<AnthropicChatProvider>());

        return services;
    }
}
