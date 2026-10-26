using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Core.Notes;

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

    /// <summary>卡片上的一行說明（ui-guide.html 第 1 節的副標）。</summary>
    public string Description => Id switch
    {
        "teal" => Strings.Theme_PaletteDesc_teal,
        "mist-blue" => Strings.Theme_PaletteDesc_mist_blue,
        "indigo" => Strings.Theme_PaletteDesc_indigo,
        "terracotta" => Strings.Theme_PaletteDesc_terracotta,
        _ => Strings.Theme_PaletteDesc_sage,
    };

    /// <summary>螢幕閱讀器念的名稱：色票名稱＋說明（不是使用中時）。</summary>
    public string AutomationName => string.Format(CultureInfo.CurrentCulture, Strings.General_PaletteAutomationFormat, Text, Description);

    /// <summary>選取（使用中）時的名稱：多一個「使用中」。</summary>
    public string AutomationNameActive => string.Format(CultureInfo.CurrentCulture, Strings.General_PaletteAutomationActiveFormat, Text, Description);

    public static IReadOnlyList<PaletteOption> All { get; } = ThemePalettes.All.Select(id => new PaletteOption(id)).ToList();
}

/// <summary>設定視窗「一般」分頁：主題（明暗）與色票。選了立即生效（由 <c>ThemeService</c> 聽 <c>IModelCatalog.Changed</c> 套用）。</summary>
public partial class GeneralViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly IFilePickerService? _files;
    private ThemeOption _selectedTheme;
    private PaletteOption _selectedPalette;

    /// <summary>已經存進設定的筆記資料夾（原文）。輸入框的文字不一定是這個值（還在編輯中）。</summary>
    private string _notesFolderSaved;

    /// <param name="files">選擇資料夾用；沒有（測試）時「選擇資料夾」什麼也不做。</param>
    public GeneralViewModel(IModelCatalog catalog, IUiDispatcher dispatcher, IFilePickerService? files = null)
    {
        _catalog = catalog;
        _files = files;
        _selectedTheme = ThemeOption.All.First(o => o.Preference == catalog.Current.Theme);
        _selectedPalette = PaletteOption.All.First(o => o.Id == ThemePalettes.Normalize(catalog.Current.ThemeId));
        _notesFolderSaved = catalog.Current.Notes.Folder;
        _notesFolderInput = _notesFolderSaved;
        _showTokenUsage = catalog.Current.Display.ShowTokenUsage;
        catalog.Changed += (_, settings) => dispatcher.Post(() =>
        {
            _selectedTheme = ThemeOption.All.First(o => o.Preference == settings.Theme);
            OnPropertyChanged(nameof(SelectedTheme));
            _selectedPalette = PaletteOption.All.First(o => o.Id == ThemePalettes.Normalize(settings.ThemeId));
            OnPropertyChanged(nameof(SelectedPalette));
            if (settings.Display.ShowTokenUsage != _showTokenUsage)
            {
                _showTokenUsage = settings.Display.ShowTokenUsage;
                OnPropertyChanged(nameof(ShowTokenUsage));
            }
            // 別處改了資料夾才覆寫輸入框；只是改了主題時不要把正在編輯的文字蓋掉
            if (settings.Notes.Folder != _notesFolderSaved)
                SetSavedNotesFolder(settings.Notes.Folder);
        });
    }

    /// <summary>筆記資料夾的預設位置（輸入框的浮水印，讓使用者看見空白時會用哪裡）。</summary>
    public string NotesFolderDefault => NoteFolderRules.DefaultFolder;

    /// <summary>目前設定的是自訂資料夾（沒有就是預設，「恢復預設」停用）。</summary>
    public bool IsNotesFolderCustomized => _notesFolderSaved.Length > 0;

    [ObservableProperty]
    private string _notesFolderInput;

    [ObservableProperty]
    private string? _notesFolderError;

    private void SetSavedNotesFolder(string folder)
    {
        _notesFolderSaved = folder;
        NotesFolderInput = folder;
        OnPropertyChanged(nameof(IsNotesFolderCustomized));
    }

    /// <summary>
    /// 套用輸入框的資料夾（Enter 或離開欄位時）。不合法時留著原文、說明原因，讓使用者改，不默默退回。
    /// 立即生效，沒有儲存鈕。
    /// </summary>
    [RelayCommand]
    public async Task CommitNotesFolderAsync()
    {
        if (NotesFolderInput.Trim() == _notesFolderSaved)
        {
            NotesFolderError = null;
            return;
        }
        await ApplyNotesFolderAsync(NotesFolderInput);
    }

    [RelayCommand]
    public async Task ChooseNotesFolderAsync()
    {
        if (_files is null)
            return;
        var folder = await _files.PickFolderAsync();
        if (folder is not null)
            await ApplyNotesFolderAsync(folder);
    }

    [RelayCommand]
    public Task ResetNotesFolderAsync() => ApplyNotesFolderAsync("");

    public async Task ApplyNotesFolderAsync(string folder)
    {
        folder = folder.Trim();
        NotesFolderError = null;
        var problem = NoteFolderRules.Validate(folder);
        if (problem is not null)
        {
            NotesFolderInput = folder;
            NotesFolderError = problem;
            return;
        }

        try
        {
            await _catalog.SetNotesFolderAsync(folder);
            SetSavedNotesFolder(folder.Trim());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            NotesFolderInput = folder;
            NotesFolderError = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
        }
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

    private bool _showTokenUsage;

    /// <summary>進階：在每則回答下方顯示 token 用量。勾選立即儲存，失敗時還原並顯示錯誤。</summary>
    public bool ShowTokenUsage
    {
        get => _showTokenUsage;
        set
        {
            if (value == _showTokenUsage)
                return;
            _showTokenUsage = value;
            OnPropertyChanged();
            _ = ApplyShowTokenUsageAsync(value);
        }
    }

    public async Task ApplyShowTokenUsageAsync(bool show)
    {
        Error = null;
        try
        {
            await _catalog.SetShowTokenUsageAsync(show);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
            _showTokenUsage = _catalog.Current.Display.ShowTokenUsage;
            OnPropertyChanged(nameof(ShowTokenUsage));
        }
    }

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
