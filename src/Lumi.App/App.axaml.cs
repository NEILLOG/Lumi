using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lumi.App.Services;
using Lumi.App.Views;
using Microsoft.Extensions.DependencyInjection;

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
            desktop.MainWindow = _services.GetRequiredService<WorkbenchWindow>();

        base.OnFrameworkInitializationCompleted();
    }
}
