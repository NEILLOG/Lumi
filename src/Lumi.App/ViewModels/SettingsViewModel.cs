using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>左側分類，數值就是選單（ListBox）的順序，也是 <see cref="SettingsViewModel.CategoryIndex"/>。</summary>
public enum SettingsCategory
{
    Home = 0,
    Connections = 1,
    Persona = 2,
    Memory = 3,
    Reminders = 4,
    Tools = 5,
    General = 6,
    Sprite = 7,
    Speech = 8,
    Network = 9,
    Privacy = 10,
}

/// <summary>設定視窗：左側分類（設定首頁、AI 連線、人格與主動性、記憶、提醒排程、工具、外觀與操作、小精靈、語音、網路、資料與隱私），「AI 連線」是一頁：連線與模型合在一份清單（<see cref="AiSettingsViewModel"/>）。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfirmService _confirm;
    private readonly ISettingsWindowService? _windows;

    public SettingsViewModel(ConnectionsViewModel connections, ModelsViewModel models, ImageGenerationViewModel imageGeneration, PersonaViewModel persona, MemoryViewModel memory,
        ToolsViewModel tools, McpServersViewModel mcpServers, GeneralViewModel general, SpriteSettingsViewModel sprite, SpeechViewModel speech, NetworkViewModel network, BrowserSettingsViewModel browser, RemindersViewModel reminders, DataPrivacyViewModel privacy, HomeViewModel home, IModelCatalog catalog, IConfirmService confirm,
        ISettingsWindowService? windows = null)
    {
        Connections = connections;
        Models = models;
        AiSettings = new AiSettingsViewModel(connections, models, imageGeneration);
        Persona = persona;
        Memory = memory;
        Tools = tools;
        McpServers = mcpServers;
        General = general;
        Sprite = sprite;
        Speech = speech;
        Network = network;
        Browser = browser;
        Reminders = reminders;
        Privacy = privacy;
        Home = home;
        home.Attach(reminders, memory, tools, general, sprite);
        home.NavigateRequested += navigation => _ = NavigateAsync(navigation);   // 待處理與首頁卡片要求切頁
        privacy.NavigateRequested += section => _ = NavigateAsync(new SettingsNavigation(section));
        _confirm = confirm;
        _windows = windows;
        LoadProblem = catalog.LoadProblem;
    }

    public ConnectionsViewModel Connections { get; }

    public ModelsViewModel Models { get; }

    /// <summary>「AI 連線」頁的清單與選取：連線、模型合成一份清單，詳細區重用 <see cref="Connections"/>、<see cref="Models"/> 的表單。</summary>
    public AiSettingsViewModel AiSettings { get; }

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

    /// <summary>瀏覽器設定（M18 18I）：放在「網路」頁的下半部，三項都是立即生效，所以不算未儲存的變更。</summary>
    public BrowserSettingsViewModel Browser { get; }

    /// <summary>「提醒排程」分類（M20）。</summary>
    public RemindersViewModel Reminders { get; }

    /// <summary>設定首頁的狀態總覽（M20）。</summary>
    public HomeViewModel Home { get; }

    /// <summary>「資料與隱私」分類（M20）。</summary>
    public DataPrivacyViewModel Privacy { get; }

    /// <summary>設定檔損毀或版本太新時的說明（版本太新時所有儲存都會失敗）。</summary>
    public string? LoadProblem { get; }

    public bool HasLoadProblem => LoadProblem is not null;

    /// <summary>左側分類，值是 <see cref="SettingsCategory"/>（預設 0＝設定首頁）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowHome), nameof(ShowConnectionsAndModels), nameof(ShowPersona), nameof(ShowMemory), nameof(ShowReminders), nameof(ShowTools), nameof(ShowGeneral), nameof(ShowSprite), nameof(ShowSpeech), nameof(ShowNetwork), nameof(ShowPrivacy))]
    private int _categoryIndex;

    /// <summary>「工具」內的分段：0 = 工具清單、1 = MCP 伺服器（M16）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowToolsListTab), nameof(ShowMcpTab))]
    private int _toolsTabIndex;

    public bool ShowToolsListTab => ToolsTabIndex == 0;

    public bool ShowMcpTab => ToolsTabIndex == 1;

    public bool ShowHome => CategoryIndex == (int)SettingsCategory.Home;

    public bool ShowConnectionsAndModels => CategoryIndex == (int)SettingsCategory.Connections;

    public bool ShowPersona => CategoryIndex == (int)SettingsCategory.Persona;

    public bool ShowMemory => CategoryIndex == (int)SettingsCategory.Memory;

    public bool ShowReminders => CategoryIndex == (int)SettingsCategory.Reminders;

    public bool ShowTools => CategoryIndex == (int)SettingsCategory.Tools;

    public bool ShowGeneral => CategoryIndex == (int)SettingsCategory.General;

    public bool ShowSprite => CategoryIndex == (int)SettingsCategory.Sprite;

    public bool ShowSpeech => CategoryIndex == (int)SettingsCategory.Speech;

    public bool ShowNetwork => CategoryIndex == (int)SettingsCategory.Network;

    public bool ShowPrivacy => CategoryIndex == (int)SettingsCategory.Privacy;

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

    public bool HasUnsavedChanges => Connections.IsDirty || Models.IsDirty || AiSettings.ImageGeneration.IsDirty || Persona.IsDirty || Speech.IsDirty || Network.IsDirty || McpServers.IsDirty;

    /// <summary>設定首頁的連結：參數是 <see cref="SettingsSection"/> 的名稱（例如 "Models"）。</summary>
    [RelayCommand]
    private async Task GoToAsync(string section)
    {
        if (Enum.TryParse<SettingsSection>(section, out var target))
            await NavigateAsync(new SettingsNavigation(target));
    }

    public async Task NavigateAsync(SettingsNavigation navigation)
    {
        switch (navigation.Section)
        {
            case SettingsSection.Home:
                CategoryIndex = (int)SettingsCategory.Home;
                break;
            case SettingsSection.Persona:
                CategoryIndex = (int)SettingsCategory.Persona;
                break;
            case SettingsSection.Memory:
                CategoryIndex = (int)SettingsCategory.Memory;
                break;
            case SettingsSection.Reminders:
                CategoryIndex = (int)SettingsCategory.Reminders;
                break;
            case SettingsSection.Tools:
                CategoryIndex = (int)SettingsCategory.Tools;
                ToolsTabIndex = 0;
                break;
            case SettingsSection.McpServers:
                CategoryIndex = (int)SettingsCategory.Tools;
                ToolsTabIndex = 1;
                break;
            case SettingsSection.General:
                CategoryIndex = (int)SettingsCategory.General;
                break;
            case SettingsSection.Sprite:
                CategoryIndex = (int)SettingsCategory.Sprite;
                break;
            case SettingsSection.Speech:
                CategoryIndex = (int)SettingsCategory.Speech;
                break;
            case SettingsSection.Network:
                CategoryIndex = (int)SettingsCategory.Network;
                break;
            case SettingsSection.Privacy:
                CategoryIndex = (int)SettingsCategory.Privacy;
                break;
            case SettingsSection.Models:
                CategoryIndex = (int)SettingsCategory.Connections;   // 連線與模型已合成一頁：預設模型選單就在頁首下方
                break;
            default:
                CategoryIndex = (int)SettingsCategory.Connections;
                await AiSettings.NavigateToConnectionAsync(navigation.ConnectionId, navigation.FocusApiKey, navigation.Notice);
                break;
        }
    }

    /// <summary>關閉視窗前：有未儲存的變更就詢問。</summary>
    public async Task<bool> ConfirmCloseAsync() =>
        !HasUnsavedChanges || await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard);
}
