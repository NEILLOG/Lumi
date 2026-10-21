using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>設定視窗：左側分類（AI 連線、人格與主動性、外觀與操作、小精靈），「AI 連線」內分連線、模型設定兩個分頁。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfirmService _confirm;
    private readonly ISettingsWindowService? _windows;

    public SettingsViewModel(ConnectionsViewModel connections, ModelsViewModel models, PersonaViewModel persona, GeneralViewModel general,
        SpriteSettingsViewModel sprite, IModelCatalog catalog, IConfirmService confirm, ISettingsWindowService? windows = null)
    {
        Connections = connections;
        Models = models;
        Persona = persona;
        General = general;
        Sprite = sprite;
        _confirm = confirm;
        _windows = windows;
        LoadProblem = catalog.LoadProblem;
    }

    public ConnectionsViewModel Connections { get; }

    public ModelsViewModel Models { get; }

    public PersonaViewModel Persona { get; }

    public GeneralViewModel General { get; }

    public SpriteSettingsViewModel Sprite { get; }

    /// <summary>設定檔損毀或版本太新時的說明（版本太新時所有儲存都會失敗）。</summary>
    public string? LoadProblem { get; }

    public bool HasLoadProblem => LoadProblem is not null;

    /// <summary>左側分類：0 = AI 連線、1 = 人格與主動性、2 = 外觀與操作、3 = 小精靈。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowConnectionsAndModels), nameof(ShowPersona), nameof(ShowGeneral), nameof(ShowSprite))]
    private int _categoryIndex;

    /// <summary>「AI 連線」內的分頁：0 = 連線、1 = 模型設定。</summary>
    [ObservableProperty]
    private int _tabIndex;

    public bool ShowConnectionsAndModels => CategoryIndex == 0;

    public bool ShowPersona => CategoryIndex == 1;

    public bool ShowGeneral => CategoryIndex == 2;

    public bool ShowSprite => CategoryIndex == 3;

    /// <summary>Menu 底部的版本說明（例如「Lumi 1.0.0」）。</summary>
    public string VersionText { get; } =
        string.Format(Strings.Settings_MenuVersion, typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>Menu 的「回到對話」：關閉設定視窗並顯示工作台（有未儲存的變更先詢問，取消就留在設定）。</summary>
    [RelayCommand]
    private async Task ReturnToChatAsync()
    {
        if (_windows is not null)
            await _windows.ReturnToWorkbench();
    }

    public bool HasUnsavedChanges => Connections.IsDirty || Models.IsDirty || Persona.IsDirty;

    public async Task NavigateAsync(SettingsNavigation navigation)
    {
        switch (navigation.Section)
        {
            case SettingsSection.Persona:
                CategoryIndex = 1;
                break;
            case SettingsSection.General:
                CategoryIndex = 2;
                break;
            case SettingsSection.Sprite:
                CategoryIndex = 3;
                break;
            case SettingsSection.Models:
                CategoryIndex = 0;
                TabIndex = 1;
                break;
            default:
                CategoryIndex = 0;
                TabIndex = 0;
                await Connections.NavigateAsync(navigation.ConnectionId, navigation.FocusApiKey, navigation.Notice);
                break;
        }
    }

    /// <summary>關閉視窗前：有未儲存的變更就詢問。</summary>
    public async Task<bool> ConfirmCloseAsync() =>
        !HasUnsavedChanges || await _confirm.ConfirmAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard);
}
