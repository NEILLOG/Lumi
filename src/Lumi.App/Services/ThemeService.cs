using Avalonia.Styling;
using Lumi.App.ViewModels;
using Lumi.Core.Models;

namespace Lumi.App.Services;

/// <summary>
/// 把設定裡的主題選擇套到整個應用程式（跟隨系統／淺色／深色，立即生效，不需重啟）。
/// 套用的動作用委派傳入，所以不需要真的有 Avalonia 應用程式就能測試。
/// </summary>
public sealed class ThemeService
{
    private readonly IModelCatalog _catalog;
    private readonly IUiDispatcher _dispatcher;

    public ThemeService(IModelCatalog catalog, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _dispatcher = dispatcher;
    }

    public static ThemeVariant ToVariant(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>啟動時先套用一次（避免先閃一下系統色），之後設定一變就更新。</summary>
    public void Attach(Action<ThemeVariant> apply)
    {
        apply(ToVariant(_catalog.Current.Theme));
        _catalog.Changed += (_, settings) => _dispatcher.Post(() => apply(ToVariant(settings.Theme)));
    }
}
