using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Platform;
using Lumi.Core.Updates;
using Lumi.Platform.Shell;
using Lumi.Sprites.Assets;
using Lumi.App.Views;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumi.App;

public partial class App : Application
{
    // 兩種建立方式：正常（DI 已組好）；或啟動前就失敗（DI 組裝失敗，只能顯示說明視窗，不碰 DI）
    private readonly IServiceProvider? _services;
    private readonly StartupFailureReport? _startupFailure;

    public App(IServiceProvider services)
    {
        _services = services;
    }

    public App(StartupFailureReport startupFailure)
    {
        _startupFailure = startupFailure;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // 唯一的 composition root：其他地方一律建構子注入
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 明確結束：小精靈模式下沒有工作台視窗也要繼續跑。「結束 Lumi」（托盤、右鍵選單）是結束路徑，
            // 另外工作台模式下關閉工作台視窗也結束（由協調者判斷，見 WorkbenchWindow）
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (_services is null)
            {
                // 啟動前就失敗：沒有 DI，只套預設色票，然後顯示說明（M9 9C）
                ThemeService.ApplyPalette(Resources.MergedDictionaries, ThemePalettes.Default);
                ShowStartupFailure(desktop, _startupFailure!);
            }
            else
            {
                StartNormally(desktop, _services);
            }

            // macOS Dock 圖示（M20 20E）：應用程式層級設定，不依賴主視窗是否已建立；失敗不影響啟動
            ApplyMacDockIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyMacDockIcon()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Lumi.App/Assets/Icons/lumi-1024.png"));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (!MacDockIcon.TryApply(buffer.ToArray()))
                _services?.GetService<ILogger<App>>()?.LogWarning("無法設定 macOS Dock 圖示");
        }
        catch (Exception ex)
        {
            _services?.GetService<ILogger<App>>()?.LogWarning(ex, "無法設定 macOS Dock 圖示");
        }
    }

    private void StartNormally(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        try
        {
            // 主題：啟動時先套用設定裡的選擇，之後設定一變就即時更新
            var themes = services.GetRequiredService<ThemeService>();
            themes.Attach(variant => RequestedThemeVariant = variant);
            themes.AttachPalette(id => ThemeService.ApplyPalette(Resources.MergedDictionaries, id));

            // 未處理例外寫進 log（M9 9C）：AppDomain、背景工作、UI 執行緒
            var hooks = services.GetRequiredService<UnhandledExceptionLogger>();
            hooks.Attach();
            Dispatcher.UIThread.UnhandledException += (_, e) => hooks.OnUiThreadException(e.Exception);
        }
        catch (Exception ex)
        {
            // 主題或例外掛鉤還沒接好就壞了：DI 可能就是有問題，套預設色票後直接用說明視窗
            ThemeService.ApplyPalette(Resources.MergedDictionaries, ThemePalettes.Default);
            ShowStartupFailure(desktop, ex);
            return;
        }

        _ = StartAsync(desktop, services);
    }

    /// <summary>
    /// 載入目前的對話（小精靈模式下工作台視窗還沒開，但小精靈要能送訊息）、接上小精靈與托盤，
    /// 然後回到上次的模式：工作台模式顯示工作台，小精靈模式只顯示小精靈。
    /// 失敗時顯示說明視窗（M9 9C），而不是讓程式無聲消失。
    /// </summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        try
        {
            // 先載入角色資料夾：上次選用的是素材型角色時，小精靈一開始就是它（不會先閃一下光球）
            await services.GetRequiredService<IReloadableSpriteCatalog>().ReloadAsync();
            await services.GetRequiredService<WorkbenchViewModel>().InitializeAsync();
            services.GetRequiredService<SpriteConversationController>().Attach();
            services.GetRequiredService<BudgetAlertCoordinator>();   // 回應結束後檢查預算（M23）
            StartPricingAutoFill(services);
            var modes = services.GetRequiredService<IAppModeCoordinator>();
            services.GetRequiredService<TrayService>().Attach(
                this, showSprite: modes.ShowSprite, openWorkbench: () => modes.OpenWorkbench(), exit: modes.Exit);
            modes.Start();
            RefreshLaunchAtLogin(services);
            StartUpdateCheck(services);
        }
        catch (Exception ex)
        {
            LogStartupFailure(services, ex);
            ShowStartupFailure(desktop, ex);
        }
    }

    /// <summary>
    /// 開機自動啟動已開啟時，把登錄的位置更新成目前這個程式（程式被搬走或重新安裝之後不會指到舊位置）。失敗不影響啟動。
    /// </summary>
    private static void RefreshLaunchAtLogin(IServiceProvider services)
    {
        try
        {
            var launch = services.GetRequiredService<ILaunchAtLogin>();
            if (launch.IsSupported && launch.IsEnabled())
                launch.SetEnabled(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            services.GetService<ILogger<App>>()?.LogWarning(ex, "無法更新開機自動啟動的位置");
        }
    }

    /// <summary>啟動後在背景檢查有沒有新版；結果透過 <see cref="IUpdateService.AvailableChanged"/> 通知工作台的提醒。失敗只記 log。</summary>
    /// <summary>升級後第一次啟動：既有模型比對價目表、帶入單價（只做一次；失敗只記 log）。</summary>
    private static void StartPricingAutoFill(IServiceProvider services)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PricingAutoFill.RunAsync(services.GetRequiredService<IModelCatalog>(), services.GetRequiredService<Lumi.Core.Usage.IPriceTableSource>(),
                    services.GetRequiredService<Lumi.Core.Chat.IConversationStore>(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<App>>()?.LogWarning(ex, "既有模型自動帶入單價失敗");
            }
        });
    }

    private static void StartUpdateCheck(IServiceProvider services)
    {
        var updates = services.GetRequiredService<IUpdateService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await updates.CheckAsync(manual: false);
            }
            catch (Exception ex)
            {
                services.GetService<ILogger<App>>()?.LogWarning(ex, "啟動時檢查更新失敗");
            }
        });
    }

    private static void LogStartupFailure(IServiceProvider services, Exception ex)
    {
        // 記錄不能再因為 DI 壞掉而失敗；拿不到 logger 就略過（說明視窗仍會顯示）
        try
        {
            services.GetRequiredService<ILogger<App>>().LogCritical(ex, "啟動失敗");
        }
        catch (Exception)
        {
        }
    }

    private void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, Exception ex) =>
        ShowStartupFailure(desktop, StartupFailureReport.Create(ex, StartupEnvironment.Current()));

    private static void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, StartupFailureReport report)
    {
        var window = new StartupFailureWindow(report);
        // 說明視窗是唯一的視窗：關閉（按「結束」或關閉鈕）就結束處理程序，結束碼非 0
        window.Closed += (_, _) => desktop.Shutdown(1);
        desktop.MainWindow = window;
        window.Show();
    }
}
