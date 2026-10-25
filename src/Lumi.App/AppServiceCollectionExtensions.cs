using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Infrastructure;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<UnhandledExceptionLogger>();   // 全域未處理例外寫進 log（M9 9C）

        // 設定視窗只會有一個：由 SettingsWindowService 開啟、關閉後丟掉，下次開啟重新建立（狀態一律從目錄重讀）
        services.AddSingleton<ISettingsWindowService, SettingsWindowService>();
        services.AddSingleton<Func<SettingsWindow>>(sp => () => sp.GetRequiredService<SettingsWindow>());
        services.AddTransient<SettingsWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ConnectionsViewModel>();
        services.AddTransient<ModelsViewModel>();
        services.AddTransient<PersonaViewModel>();
        services.AddTransient<McpServersViewModel>();   // MCP 伺服器（M16 16F）
        services.AddTransient<ToolsViewModel>();    // 工具清單（M15 15D）
        services.AddTransient<MemoryViewModel>();   // 記憶（M14 14D）
        services.AddTransient<GeneralViewModel>();
        services.AddTransient<SpriteSettingsViewModel>();
        services.AddTransient<SpeechViewModel>();
        services.AddTransient<NetworkViewModel>();   // 網路與 Proxy（M17 17G）

        // 權限確認：取代 Core 預設的「一律拒絕」；對話迴圈等在 IToolApprovalService，卡片上的按鈕走 IToolApprovalResponder
        services.AddSingleton<ToolApprovalService>();
        services.Replace(ServiceDescriptor.Singleton<IToolApprovalService>(sp => sp.GetRequiredService<ToolApprovalService>()));
        services.AddSingleton<IToolApprovalResponder>(sp => sp.GetRequiredService<ToolApprovalService>());

        services.AddHostedService<AttachmentCleanupService>();
        services.AddHostedService<MemoryCleanupService>();        // 啟動時清掉到期超過 30 天的記憶（M14）
        services.AddHostedService<SearchTextBackfillService>();   // 舊訊息的搜尋文字，背景補一次
        services.AddHostedService<McpHostedService>();            // 背景連上已啟用的 MCP 伺服器（M16）
        services.AddHostedService<ReminderHostedService>();       // 提醒的背景迴圈（M10 10D）：到點觸發、錯過的檢查、睡眠喚醒
        // 提醒的送達（M10 10E）：取代 Core 的 NullNotificationSink；工作台的通知列與小精靈的提醒卡片綁同一個執行個體。
        // 視窗與協調者用委派延遲取得，避免「工具 → 提醒服務 → 通知 → 協調者 → 對話 → 工具」的循環相依
        services.AddSingleton<NotificationCenter>(sp => new NotificationCenter(
            sp.GetRequiredService<IUiDispatcher>(),
            () => sp.GetRequiredService<IWindowHost>(),
            () => sp.GetRequiredService<IAppModeCoordinator>(),
            sp.GetRequiredService<ILogger<NotificationCenter>>()));
        services.Replace(ServiceDescriptor.Singleton<INotificationSink>(sp => sp.GetRequiredService<NotificationCenter>()));

        // 輸入框上方的上下文列：選擇器與剪貼簿圖片由畫面層實作
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IClipboardImageService, ClipboardImageService>();
        services.AddSingleton<ContextBarViewModel>();
        // 語音輸入（M6 6D）：工作台與小精靈共用同一個控制器；App 結束時由容器釋放（會取消錄音）
        services.AddSingleton<IVoiceInputController, VoiceInputController>();

        services.AddSingleton<IApiKeyPrompt, SettingsApiKeyPrompt>();
        // 小精靈的殼（M7）與內建的光球角色；殼的數字都在 SpriteShellOptions
        services.AddSingleton(new SpriteShellOptions());
        services.AddSprite<OrbSpriteFactory>();
        // 素材型角色（M8）：角色資料夾在使用者資料夾下的 sprites/，啟動時掃描一次，設定頁可以重新載入
        services.AddLumiAssetSprites(o => o.Directory = Path.Combine(LumiPaths.DataDirectory, "sprites"));
        services.AddSingleton<IFolderOpener, ProcessFolderOpener>();
        services.AddSingleton<SpriteStateMapper>();
        services.AddSingleton<SpriteBubbleViewModel>();
        services.AddSingleton<SpriteWindow>();
        services.AddSingleton<SpriteConversationController>();
        services.AddSingleton<TrayService>();
        // 模式與視窗：兩種模式互斥，規則集中在協調者；真的視窗由 WindowHost 延遲取得
        services.AddSingleton<IWindowHost, WindowHost>();
        services.AddSingleton<IAppModeCoordinator, AppModeCoordinator>();
        // 角色語氣（prompt 第 4 層）：取代 Core 預設的「沒有語氣」
        services.Replace(ServiceDescriptor.Singleton<Lumi.Core.Prompts.ISpriteVoiceProvider, SpriteVoiceProvider>());

        services.AddSingleton<WorkbenchViewModel>();
        services.AddTransient<WorkbenchWindow>();
        return services;
    }
}
