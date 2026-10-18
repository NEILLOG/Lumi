using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>設定視窗：左側分類（連線與模型、一般），「連線與模型」內分連線、模型設定兩個分頁。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfirmService _confirm;

    public SettingsViewModel(ConnectionsViewModel connections, ModelsViewModel models, GeneralViewModel general,
        IModelCatalog catalog, IConfirmService confirm)
    {
        Connections = connections;
        Models = models;
        General = general;
        _confirm = confirm;
        LoadProblem = catalog.LoadProblem;
    }

    public ConnectionsViewModel Connections { get; }

    public ModelsViewModel Models { get; }

    public GeneralViewModel General { get; }

    /// <summary>設定檔損毀或版本太新時的說明（版本太新時所有儲存都會失敗）。</summary>
    public string? LoadProblem { get; }

    public bool HasLoadProblem => LoadProblem is not null;

    /// <summary>左側分類：0 = 連線與模型、1 = 一般。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowConnectionsAndModels), nameof(ShowGeneral))]
    private int _categoryIndex;

    /// <summary>「連線與模型」內的分頁：0 = 連線、1 = 模型設定。</summary>
    [ObservableProperty]
    private int _tabIndex;

    public bool ShowConnectionsAndModels => CategoryIndex == 0;

    public bool ShowGeneral => CategoryIndex == 1;

    public bool HasUnsavedChanges => Connections.IsDirty || Models.IsDirty;

    public async Task NavigateAsync(SettingsNavigation navigation)
    {
        switch (navigation.Section)
        {
            case SettingsSection.General:
                CategoryIndex = 1;
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
