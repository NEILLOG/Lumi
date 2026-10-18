using Lumi.App.ViewModels;
using Lumi.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.App;

public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddLumiApp(this IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IApiKeyPrompt, ApiKeyPromptService>();
        services.AddSingleton<WorkbenchViewModel>();
        services.AddTransient<WorkbenchWindow>();
        return services;
    }
}
