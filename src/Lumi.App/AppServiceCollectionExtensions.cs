using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Providers.Anthropic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lumi.App;

public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddLumiApp(this IServiceCollection services)
    {
        // M1：模型設定來自 appsettings.json；M2 由 IModelCatalog 取代
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AnthropicDevOptions>>().Value;
            return new ChatDefaults(options.Model, options.MaxTokens, options.ContextWindow);
        });

        services.AddSingleton<IApiKeyPrompt, ApiKeyPromptService>();
        services.AddSingleton<WorkbenchViewModel>();
        services.AddTransient<WorkbenchWindow>();
        return services;
    }
}
