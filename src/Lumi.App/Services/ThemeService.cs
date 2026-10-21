using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
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

    /// <summary>
    /// 主題色票（ui-guide.html）：啟動時套用一次，之後色票 ID 一變才重新套用。
    /// 其他設定（連線、模型…）變更時不重複替換資源。
    /// </summary>
    public void AttachPalette(Action<string> apply)
    {
        var applied = ThemePalettes.Normalize(_catalog.Current.ThemeId);
        apply(applied);
        _catalog.Changed += (_, settings) => _dispatcher.Post(() =>
        {
            var next = ThemePalettes.Normalize(settings.ThemeId);
            if (next == applied)
                return;
            applied = next;
            apply(next);
        });
    }

    /// <summary>
    /// 把色票換成 <paramref name="paletteId"/>：移除舊的色票檔，加入新的。元件用 DynamicResource，會即時更新。
    /// 放在 <paramref name="merged"/> 最前面，讓其他 token 檔照常覆蓋。
    /// </summary>
    public static void ApplyPalette(IList<IResourceProvider> merged, string paletteId)
    {
        var id = ThemePalettes.Normalize(paletteId);
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is ResourceInclude { Source: { } source } && source.OriginalString.Contains("/Themes/Palettes/", StringComparison.Ordinal))
                merged.RemoveAt(i);
        }
        merged.Insert(0, new ResourceInclude(new Uri("avares://Lumi.App/"))
        {
            Source = new Uri($"avares://Lumi.App/Themes/Palettes/{id}.axaml"),
        });
    }
}
