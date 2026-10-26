using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;
using Lumi.Core.Web;

namespace Lumi.App.ViewModels;

/// <summary>設定首頁的一張狀態卡片。點了用 <see cref="SettingsViewModel.GoToCommand"/> 切到 <see cref="Section"/>。</summary>
public sealed partial class HomeCardViewModel : ObservableObject
{
    public HomeCardViewModel(string section, string iconKey, string title)
    {
        Section = section;
        IconKey = iconKey;
        Title = title;
    }

    /// <summary><see cref="SettingsSection"/> 的名稱（例如 "Models"），給 GoToCommand 當參數。</summary>
    public string Section { get; }

    /// <summary>圖示資源的 key（例如 "LumiIconDiamond"），由 XAML 的 converter 取得幾何。</summary>
    public string IconKey { get; }

    public string Title { get; }

    [ObservableProperty]
    private string _value = "";

    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private bool _needsAttention;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>
/// 設定首頁的狀態總覽：6 張卡片（AI 連線、提醒排程、記憶、工具、外觀與操作、網路），內容即時反映設定與狀態。
/// 只讀，不新增任何設定開關。建構子與 <see cref="Attach"/> 的簽章固定，DI 與 SettingsViewModel 依賴它。
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly IModelCatalog _catalog;
    private readonly ConnectionStatusCache _status;
    private readonly IUiDispatcher _dispatcher;
    private readonly HomeCardViewModel _connections;
    private readonly HomeCardViewModel _reminders;
    private readonly HomeCardViewModel _memory;
    private readonly HomeCardViewModel _tools;
    private readonly HomeCardViewModel _general;
    private readonly HomeCardViewModel _network;
    private readonly EventHandler<AppSettings> _onCatalogChanged;
    private readonly EventHandler _onStatusChanged;
    private readonly NotifyCollectionChangedEventHandler _onRemindersChanged;
    private readonly NotifyCollectionChangedEventHandler _onToolGroupsChanged;
    private readonly PropertyChangedEventHandler _onMemoryChanged;
    private readonly PropertyChangedEventHandler _onGeneralChanged;
    private readonly PropertyChangedEventHandler _onSpriteChanged;

    private RemindersViewModel? _remindersVm;
    private MemoryViewModel? _memoryVm;
    private ToolsViewModel? _toolsVm;
    private GeneralViewModel? _generalVm;
    private SpriteSettingsViewModel? _spriteVm;
    private int _generation;
    private bool _disposed;

    public HomeViewModel(IModelCatalog catalog, ConnectionStatusCache status, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _status = status;
        _dispatcher = dispatcher;

        _connections = new HomeCardViewModel(nameof(SettingsSection.Connections), "LumiIconDiamond", Strings.Settings_CategoryConnections);
        _reminders = new HomeCardViewModel(nameof(SettingsSection.Reminders), "LumiIconBell", Strings.Settings_CategoryReminders);
        _memory = new HomeCardViewModel(nameof(SettingsSection.Memory), "LumiIconBrain", Strings.Settings_CategoryMemory);
        _tools = new HomeCardViewModel(nameof(SettingsSection.Tools), "LumiIconWrench", Strings.Tools_MenuTitle);
        _general = new HomeCardViewModel(nameof(SettingsSection.General), "LumiIconSun", Strings.Home_AppearanceTitle);
        _network = new HomeCardViewModel(nameof(SettingsSection.Network), "LumiIconGlobe", Strings.Settings_CategoryNetwork);
        Cards = [_connections, _reminders, _memory, _tools, _general, _network];

        _onCatalogChanged = (_, _) => _dispatcher.Post(OnExternalChange);
        _onStatusChanged = (_, _) => _dispatcher.Post(OnExternalChange);
        _onRemindersChanged = (_, _) => OnChanged();
        _onToolGroupsChanged = (_, _) => OnChanged();
        _onMemoryChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(MemoryViewModel.IsEnabled) or nameof(MemoryViewModel.UsageText))
                OnChanged();
        };
        _onGeneralChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralViewModel.SelectedPalette))
                OnChanged();
        };
        _onSpriteChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(SpriteSettingsViewModel.SelectedSprite))
                OnChanged();
        };

        _catalog.Changed += _onCatalogChanged;
        _status.Changed += _onStatusChanged;
        Refresh();
    }

    /// <summary>6 張卡片，順序固定：AI 連線、提醒排程、記憶、工具、外觀與操作、網路。</summary>
    public IReadOnlyList<HomeCardViewModel> Cards { get; }

    /// <summary>SettingsViewModel 建構時呼叫一次，把其他分類的 ViewModel 交給首頁讀取即時狀態。</summary>
    public void Attach(RemindersViewModel reminders, MemoryViewModel memory, ToolsViewModel tools, GeneralViewModel general, SpriteSettingsViewModel sprite)
    {
        if (_disposed)
            return;

        _remindersVm = reminders;
        _memoryVm = memory;
        _toolsVm = tools;
        _generalVm = general;
        _spriteVm = sprite;

        reminders.Pending.CollectionChanged += _onRemindersChanged;
        memory.PropertyChanged += _onMemoryChanged;
        tools.Groups.CollectionChanged += _onToolGroupsChanged;
        general.PropertyChanged += _onGeneralChanged;
        sprite.PropertyChanged += _onSpriteChanged;

        Refresh();
    }

    /// <summary>重新計算全部卡片。金鑰結果還沒回來時先當作有金鑰；較晚開始的刷新讓較早的結果作廢。</summary>
    public async Task RefreshAsync()
    {
        if (_disposed)
            return;

        var generation = ++_generation;
        var settings = _catalog.Current;
        ApplyAll(settings, hasApiKey: true);

        var workbench = ResolveWorkbench(settings);
        if (workbench is null)
            return;

        var hasApiKey = await _catalog.HasApiKeyAsync(workbench.Value.Connection.Id);
        if (_disposed || generation != _generation)
            return;
        ApplyAll(settings, hasApiKey);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _catalog.Changed -= _onCatalogChanged;
        _status.Changed -= _onStatusChanged;
        if (_remindersVm is not null)
            _remindersVm.Pending.CollectionChanged -= _onRemindersChanged;
        if (_memoryVm is not null)
            _memoryVm.PropertyChanged -= _onMemoryChanged;
        if (_toolsVm is not null)
            _toolsVm.Groups.CollectionChanged -= _onToolGroupsChanged;
        if (_generalVm is not null)
            _generalVm.PropertyChanged -= _onGeneralChanged;
        if (_spriteVm is not null)
            _spriteVm.PropertyChanged -= _onSpriteChanged;
    }

    /// <summary>目錄與連線狀態的事件可能在背景執行緒上來，回到 UI 執行緒後才刷新。</summary>
    private void OnExternalChange()
    {
        if (!_disposed)
            Refresh();
    }

    private void OnChanged()
    {
        if (!_disposed)
            Refresh();
    }

    private void Refresh() => _ = RefreshAsync();

    /// <summary>工作台預設模型與它的連線；找不到模型或連線時回 null。</summary>
    private static (ModelSettings Model, ConnectionSettings Connection)? ResolveWorkbench(AppSettings settings)
    {
        var model = settings.Models.FirstOrDefault(m => m.Id == settings.WorkbenchModelId);
        if (model is null)
            return null;
        var connection = settings.Connections.FirstOrDefault(c => c.Id == model.ConnectionId);
        return connection is null ? null : (model, connection);
    }

    private void ApplyAll(AppSettings settings, bool hasApiKey)
    {
        ApplyConnections(settings, hasApiKey);
        ApplyReminders();
        ApplyMemory();
        ApplyTools();
        ApplyAppearance(settings);
        ApplyNetwork(settings);
    }

    private void ApplyConnections(AppSettings settings, bool hasApiKey)
    {
        var workbench = ResolveWorkbench(settings);
        if (workbench is null)
        {
            _connections.Value = Strings.Home_NoModel;
            _connections.Detail = null;
            _connections.NeedsAttention = true;
            return;
        }

        var (model, connection) = workbench.Value;
        var status = _status.Get(connection.Id).Status;
        var statusText = !hasApiKey ? Strings.Home_NoApiKey : status switch
        {
            ConnectionStatus.Ok => Strings.Home_StatusOk,
            ConnectionStatus.Failed => Strings.Home_StatusFailed,
            _ => Strings.Home_StatusUnknown,
        };

        _connections.Value = model.DisplayName;
        _connections.Detail = string.Format(CultureInfo.CurrentCulture, Strings.Home_ModelDetailFormat, connection.Name, model.ModelId)
            + " · " + statusText;
        _connections.NeedsAttention = !hasApiKey || status == ConnectionStatus.Failed;
    }

    private void ApplyReminders()
    {
        if (_remindersVm is null)
        {
            _reminders.Value = "";
            _reminders.Detail = null;
            _reminders.NeedsAttention = false;
            return;
        }

        var pending = _remindersVm.Pending;
        var first = pending.FirstOrDefault();
        _reminders.Value = pending.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Home_RemindersCountFormat, pending.Count)
            : Strings.Home_RemindersNone;

        string? detail = null;
        if (first is not null)
        {
            detail = string.Format(CultureInfo.CurrentCulture, Strings.Home_RemindersNextFormat, first.Title);
            if (!string.IsNullOrEmpty(first.ScheduleText))
                detail += " · " + first.ScheduleText;
        }
        _reminders.Detail = detail;
        _reminders.NeedsAttention = false;
    }

    private void ApplyMemory()
    {
        if (_memoryVm is null)
        {
            _memory.Value = "";
            _memory.Detail = null;
            _memory.NeedsAttention = false;
            return;
        }

        _memory.Value = _memoryVm.IsEnabled ? Strings.Home_MemoryOn : Strings.Home_MemoryOff;
        _memory.Detail = _memoryVm.UsageText;
        _memory.NeedsAttention = false;
    }

    private void ApplyTools()
    {
        if (_toolsVm is null)
        {
            _tools.Value = "";
            _tools.Detail = null;
            _tools.NeedsAttention = false;
            return;
        }

        var items = _toolsVm.Groups.SelectMany(g => g.Items).ToList();
        var enabled = items.Count(i => i.IsEnabled);
        _tools.Value = string.Format(CultureInfo.CurrentCulture, Strings.Home_ToolsCountFormat, enabled, items.Count);
        _tools.Detail = null;
        _tools.NeedsAttention = false;
    }

    private void ApplyAppearance(AppSettings settings)
    {
        if (_generalVm is null || _spriteVm is null)
        {
            _general.Value = "";
            _general.Detail = null;
            _general.NeedsAttention = false;
            return;
        }

        var theme = settings.Theme switch
        {
            ThemePreference.Light => Strings.Theme_Light,
            ThemePreference.Dark => Strings.Theme_Dark,
            _ => Strings.Theme_System,
        };
        _general.Value = string.Format(CultureInfo.CurrentCulture, Strings.Home_AppearanceFormat, _generalVm.SelectedPalette.Text, theme);
        _general.Detail = string.Format(CultureInfo.CurrentCulture, Strings.Home_SpriteFormat, _spriteVm.SelectedSprite.Name);
        _general.NeedsAttention = false;
    }

    private void ApplyNetwork(AppSettings settings)
    {
        _network.Value = settings.Network.Mode switch
        {
            ProxyMode.Pac => Strings.Network_ModePac,
            ProxyMode.Manual => Strings.Network_ModeManual,
            ProxyMode.None => Strings.Network_ModeNone,
            _ => Strings.Network_ModeSystem,
        };
        _network.Detail = null;
        _network.NeedsAttention = false;
    }
}
