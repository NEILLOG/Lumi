using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumi.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // 唯一的 composition root：其他地方一律建構子注入
    public override void OnFrameworkInitializationCompleted()
    {
        // 主題：啟動時先套用設定裡的選擇，之後設定一變就即時更新
        _services.GetRequiredService<ThemeService>().Attach(variant => RequestedThemeVariant = variant);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 明確結束：小精靈模式下沒有工作台視窗也要繼續跑。「結束 Lumi」（托盤、右鍵選單）是結束路徑，
            // 另外工作台模式下關閉工作台視窗也結束（由協調者判斷，見 WorkbenchWindow）
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 載入目前的對話（小精靈模式下工作台視窗還沒開，但小精靈要能送訊息）、接上小精靈與托盤，
    /// 然後回到上次的模式：工作台模式顯示工作台，小精靈模式只顯示小精靈。
    /// </summary>
    private async Task StartAsync()
    {
        try
        {
            await _services.GetRequiredService<WorkbenchViewModel>().InitializeAsync();
            _services.GetRequiredService<SpriteConversationController>().Attach();
            var modes = _services.GetRequiredService<IAppModeCoordinator>();
            _services.GetRequiredService<TrayService>().Attach(
                this, showSprite: modes.ShowSprite, openWorkbench: () => modes.OpenWorkbench(), exit: modes.Exit);
            modes.Start();
        }
        catch (Exception ex)
        {
            _services.GetRequiredService<ILogger<App>>().LogCritical(ex, "啟動失敗");
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(1);
        }
    }
}
