using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

public sealed record ThemeOption(ThemePreference Preference)
{
    public string Text => Preference switch
    {
        ThemePreference.Light => Strings.Theme_Light,
        ThemePreference.Dark => Strings.Theme_Dark,
        _ => Strings.Theme_System,
    };

    public static IReadOnlyList<ThemeOption> All { get; } =
        [new(ThemePreference.System), new(ThemePreference.Light), new(ThemePreference.Dark)];
}

/// <summary>主題色票（ui-guide.html 第 1 節）。Id 對應 <see cref="ThemePalettes"/>，名稱在資源檔。</summary>
public sealed record PaletteOption(string Id)
{
    public string Text => Id switch
    {
        "teal" => Strings.Theme_Palette_teal,
        "mist-blue" => Strings.Theme_Palette_mist_blue,
        "indigo" => Strings.Theme_Palette_indigo,
        "terracotta" => Strings.Theme_Palette_terracotta,
        _ => Strings.Theme_Palette_sage,
    };

    public static IReadOnlyList<PaletteOption> All { get; } = ThemePalettes.All.Select(id => new PaletteOption(id)).ToList();
}

/// <summary>設定視窗「一般」分頁：主題（明暗）與色票。選了立即生效（由 <c>ThemeService</c> 聽 <c>IModelCatalog.Changed</c> 套用）。</summary>
public partial class GeneralViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private ThemeOption _selectedTheme;
    private PaletteOption _selectedPalette;

    public GeneralViewModel(IModelCatalog catalog, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _selectedTheme = ThemeOption.All.First(o => o.Preference == catalog.Current.Theme);
        _selectedPalette = PaletteOption.All.First(o => o.Id == ThemePalettes.Normalize(catalog.Current.ThemeId));
        catalog.Changed += (_, settings) => dispatcher.Post(() =>
        {
            _selectedTheme = ThemeOption.All.First(o => o.Preference == settings.Theme);
            OnPropertyChanged(nameof(SelectedTheme));
            _selectedPalette = PaletteOption.All.First(o => o.Id == ThemePalettes.Normalize(settings.ThemeId));
            OnPropertyChanged(nameof(SelectedPalette));
        });
    }

    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;

    public IReadOnlyList<PaletteOption> Palettes => PaletteOption.All;

    public PaletteOption SelectedPalette
    {
        get => _selectedPalette;
        set
        {
            if (value is null || value == _selectedPalette)
                return;
            _selectedPalette = value;
            OnPropertyChanged();
            _ = ApplyPaletteAsync(value.Id);
        }
    }

    public async Task ApplyPaletteAsync(string paletteId)
    {
        Error = null;
        try
        {
            await _catalog.SetThemeIdAsync(paletteId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
            _selectedPalette = PaletteOption.All.First(o => o.Id == ThemePalettes.Normalize(_catalog.Current.ThemeId));
            OnPropertyChanged(nameof(SelectedPalette));
        }
    }

    [ObservableProperty]
    private string? _error;

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || value == _selectedTheme)
                return;
            _selectedTheme = value;
            OnPropertyChanged();
            _ = ApplyAsync(value.Preference);
        }
    }

    public async Task ApplyAsync(ThemePreference preference)
    {
        Error = null;
        try
        {
            await _catalog.SetThemeAsync(preference);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
            _selectedTheme = ThemeOption.All.First(o => o.Preference == _catalog.Current.Theme);
            OnPropertyChanged(nameof(SelectedTheme));
        }
    }
}
