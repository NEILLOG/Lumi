using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>設定視窗：左側分類（AI 連線、人格與主動性、記憶、工具、外觀與操作、小精靈、語音），「AI 連線」內分連線、模型設定兩個分頁。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfirmService _confirm;
    private readonly ISettingsWindowService? _windows;

    public SettingsViewModel(ConnectionsViewModel connections, ModelsViewModel models, PersonaViewModel persona, MemoryViewModel memory,
        ToolsViewModel tools, McpServersViewModel mcpServers, GeneralViewModel general, SpriteSettingsViewModel sprite, SpeechViewModel speech, NetworkViewModel network, IModelCatalog catalog, IConfirmService confirm,
        ISettingsWindowService? windows = null)
    {
        Connections = connections;
        Models = models;
        Persona = persona;
        Memory = memory;
        Tools = tools;
        McpServers = mcpServers;
        General = general;
        Sprite = sprite;
        Speech = speech;
        Network = network;
        _confirm = confirm;
        _windows = windows;
        LoadProblem = catalog.LoadProblem;
    }

    public ConnectionsViewModel Connections { get; }

    public ModelsViewModel Models { get; }

    public PersonaViewModel Persona { get; }

    /// <summary>「記憶」分類（M14 14D）。</summary>
    public MemoryViewModel Memory { get; }

    /// <summary>「工具」分類（M15）。</summary>
    public ToolsViewModel Tools { get; }

    /// <summary>「工具」分類裡的「MCP 伺服器」分段（M16 16F）。</summary>
    public McpServersViewModel McpServers { get; }

    public GeneralViewModel General { get; }

    public SpriteSettingsViewModel Sprite { get; }

    public SpeechViewModel Speech { get; }

    /// <summary>網路與 Proxy（M17 17G）。</summary>
    public NetworkViewModel Network { get; }

    /// <summary>設定檔損毀或版本太新時的說明（版本太新時所有儲存都會失敗）。</summary>
    public string? LoadProblem { get; }

    public bool HasLoadProblem => LoadProblem is not null;

    /// <summary>左側分類：0 = AI 連線、1 = 人格與主動性、2 = 記憶、3 = 工具、4 = 外觀與操作、5 = 小精靈、6 = 語音、7 = 網路。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowConnectionsAndModels), nameof(ShowPersona), nameof(ShowMemory), nameof(ShowTools), nameof(ShowGeneral), nameof(ShowSprite), nameof(ShowSpeech), nameof(ShowNetwork))]
    private int _categoryIndex;

    /// <summary>「AI 連線」內的分頁：0 = 連線、1 = 模型設定。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowConnectionsTab), nameof(ShowModelsTab))]
    private int _tabIndex;

    /// <summary>分段切換「連線」被選取（頁首的主要動作是「新增連線」）。</summary>
    public bool ShowConnectionsTab => TabIndex == 0;

    /// <summary>分段切換「模型設定」被選取（頁首的主要動作是「新增模型設定」）。</summary>
    public bool ShowModelsTab => TabIndex == 1;

    /// <summary>「工具」內的分段：0 = 工具清單、1 = MCP 伺服器（M16）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowToolsListTab), nameof(ShowMcpTab))]
    private int _toolsTabIndex;

    public bool ShowToolsListTab => ToolsTabIndex == 0;

    public bool ShowMcpTab => ToolsTabIndex == 1;

    public bool ShowConnectionsAndModels => CategoryIndex == 0;

    public bool ShowPersona => CategoryIndex == 1;

    public bool ShowMemory => CategoryIndex == 2;

    public bool ShowTools => CategoryIndex == 3;

    public bool ShowGeneral => CategoryIndex == 4;

    public bool ShowSprite => CategoryIndex == 5;

    public bool ShowSpeech => CategoryIndex == 6;

    public bool ShowNetwork => CategoryIndex == 7;

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

    public bool HasUnsavedChanges => Connections.IsDirty || Models.IsDirty || Persona.IsDirty || Speech.IsDirty || Network.IsDirty || McpServers.IsDirty;

    public async Task NavigateAsync(SettingsNavigation navigation)
    {
        switch (navigation.Section)
        {
            case SettingsSection.Persona:
                CategoryIndex = 1;
                break;
            case SettingsSection.Tools:
                CategoryIndex = 3;
                ToolsTabIndex = 0;
                break;
            case SettingsSection.McpServers:
                CategoryIndex = 3;
                ToolsTabIndex = 1;
                break;
            case SettingsSection.General:
                CategoryIndex = 4;
                break;
            case SettingsSection.Sprite:
                CategoryIndex = 5;
                break;
            case SettingsSection.Speech:
                CategoryIndex = 6;
                break;
            case SettingsSection.Network:
                CategoryIndex = 7;
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
        !HasUnsavedChanges || await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard);
}
