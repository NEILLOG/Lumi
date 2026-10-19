using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.App;

public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddLumiApp(this IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfirmService, ConfirmService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
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

        // 權限確認：取代 Core 預設的「一律拒絕」；對話迴圈等在 IToolApprovalService，卡片上的按鈕走 IToolApprovalResponder
        services.AddSingleton<ToolApprovalService>();
        services.Replace(ServiceDescriptor.Singleton<IToolApprovalService>(sp => sp.GetRequiredService<ToolApprovalService>()));
        services.AddSingleton<IToolApprovalResponder>(sp => sp.GetRequiredService<ToolApprovalService>());

        services.AddHostedService<AttachmentCleanupService>();

        services.AddSingleton<IApiKeyPrompt, SettingsApiKeyPrompt>();
        services.AddSingleton<WorkbenchViewModel>();
        services.AddTransient<WorkbenchWindow>();
        return services;
    }
}
