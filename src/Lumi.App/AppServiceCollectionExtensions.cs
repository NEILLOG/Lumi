using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.App;

public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddLumiApp(this IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IConfirmService, ConfirmService>();
        services.AddSingleton<ConnectionStatusCache>();
        services.AddSingleton<ThemeService>();

        // 設定視窗只會有一個：由 SettingsWindowService 開啟、關閉後丟掉，下次開啟重新建立（狀態一律從目錄重讀）
        services.AddSingleton<ISettingsWindowService, SettingsWindowService>();
        services.AddSingleton<Func<SettingsWindow>>(sp => () => sp.GetRequiredService<SettingsWindow>());
        services.AddTransient<SettingsWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ConnectionsViewModel>();
        services.AddTransient<ModelsViewModel>();
        services.AddTransient<GeneralViewModel>();

        services.AddSingleton<IApiKeyPrompt, SettingsApiKeyPrompt>();
        services.AddSingleton<WorkbenchViewModel>();
        services.AddTransient<WorkbenchWindow>();
        return services;
    }
}
