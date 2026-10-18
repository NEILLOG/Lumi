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

/// <summary>設定視窗「一般」分頁：主題。選了立即生效（由 <c>ThemeService</c> 聽 <c>IModelCatalog.Changed</c> 套用）。</summary>
public partial class GeneralViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private ThemeOption _selectedTheme;

    public GeneralViewModel(IModelCatalog catalog, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _selectedTheme = ThemeOption.All.First(o => o.Preference == catalog.Current.Theme);
        catalog.Changed += (_, settings) => dispatcher.Post(() =>
        {
            _selectedTheme = ThemeOption.All.First(o => o.Preference == settings.Theme);
            OnPropertyChanged(nameof(SelectedTheme));
        });
    }

    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;

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
