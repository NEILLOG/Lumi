using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Core.Mcp;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Lumi.Core.Usage;
using Lumi.Core.Web;

namespace Lumi.App.ViewModels;

/// <summary>首頁「待處理」的一列。<see cref="ActionCommand"/> 已綁好：前往設定，或就地測試連線。</summary>
public sealed record HomePendingItem(string Id, string IconKey, string Title, string Description, string ActionText, bool IsPrimary, ICommand ActionCommand);

/// <summary>首頁「目前模型」卡片。<see cref="ShowSharedNote"/> 只在工作台與小精靈確定用同一個模型時為 true。</summary>
public sealed record HomeModelCard(string Name, string? DetailText, string? StatusText, bool NeedsAttention, bool ShowSharedNote, string ModelId = "")
{
    /// <summary>卡片中央用等寬字顯示的文字：確切的 model ID；沒有模型時是提示文字。</summary>
    public string MonoText => ModelId.Length > 0 ? ModelId : Name;

    public static HomeModelCard Empty { get; } = new("", null, null, false, false);
}

/// <summary>首頁「記憶」卡片：純文字與進度（0–1），沒有開關。</summary>
public sealed record HomeMemoryCard(string StatusText, string ItemsText, double ItemsProgress, string CharsText, double CharsProgress)
{
    public static HomeMemoryCard Empty { get; } = new("", "", 0, "", 0);
}

/// <summary>
/// 首頁「本月用量」卡片。有 <see cref="IUsageStore"/> 時 <see cref="MonthText"/> 是本月新台幣金額（決定 19）；沒有時是 tokens 縮寫（<see cref="ShowTokensUnit"/> 為 true）。
/// <see cref="BudgetText"/> 為 null 代表沒設預算：不顯示預算列與進度條。<see cref="BudgetRatio"/> 0–1，超過 100% 時為 1。
/// </summary>
public sealed record HomeUsageCard(bool IsLoading, bool IsUnavailable, string? MonthText, string? TodayText, string? InputText, string? OutputText,
    string? BudgetText = null, double? BudgetRatio = null, UsageLevel BudgetLevel = UsageLevel.Normal, bool ShowTokensUnit = false)
{
    public static HomeUsageCard Loading { get; } = new(true, false, null, null, null, null);

    public static HomeUsageCard Unavailable { get; } = new(false, true, null, null, null, null);

    /// <summary>進度條用：預算等級分三條，只顯示對應的那一條（顏色規則同用量頁）。</summary>
    public bool BudgetIsNormal => BudgetLevel == UsageLevel.Normal;

    public bool BudgetIsWarning => BudgetLevel == UsageLevel.Warning;

    public bool BudgetIsDanger => BudgetLevel == UsageLevel.Danger;
}

/// <summary>首頁「工具」卡片裡的一台 MCP 伺服器。<see cref="ReconnectCommand"/> 只在失敗時可用。</summary>
public sealed record HomeMcpServerItem(string ServerId, string Name, bool IsConnected, string StatusText, bool CanReconnect, ICommand ReconnectCommand);

/// <summary>首頁「工具」卡片：內建工具與 MCP 伺服器的數字，以及最多 4 台的伺服器列。</summary>
public sealed record HomeToolsCard(int BuiltInEnabled, int BuiltInTotal, string BuiltInText, int McpConnected, int McpTotal, string McpText,
    IReadOnlyList<HomeMcpServerItem> McpServers, string? MoreText)
{
    public static HomeToolsCard Empty { get; } = new(0, 0, "", 0, 0, "", [], null);
}

/// <summary>首頁「提醒排程」卡片。計時器不算進 <see cref="PendingCount"/>。</summary>
public sealed record HomeRemindersCard(int PendingCount, int TimerCount, string NextText, bool HasNext)
{
    public static HomeRemindersCard Empty { get; } = new(0, 0, "", false);
}

public enum HomeActionStatus { Succeeded, Failed, Denied }

/// <summary>首頁「最近的動作」的一列。<paramref name="Summary"/> 在失敗時是說明文字。</summary>
public sealed record HomeActionItem(string RelativeTime, HomeActionStatus Status, string DisplayName, string? Target, string? Summary, string? ApprovalBadge);

/// <summary>
/// 設定首頁的狀態總覽：待處理、目前模型、記憶、本月用量、工具、提醒排程、最近的動作，以及側欄橘點用的旗標。
/// 只讀（待處理的「測試連線」除外，它會寫入連線狀態快取），不新增任何設定開關。
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private const string McpGroupPrefix = "mcp:";
    private const int RecentActionLimit = 5;
    private const int McpRowLimit = 4;

    private readonly IModelCatalog _catalog;
    private readonly ConnectionStatusCache _status;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConversationStore _conversations;
    private readonly IMcpToolSource _mcp;
    private readonly IConnectionTester _tester;
    private readonly TimeProvider _time;
    private readonly MemoryOptions _memoryOptions;
    private readonly IUsageStore? _usage;
    private readonly EventHandler<AppSettings> _onCatalogChanged;
    private readonly EventHandler _onStatusChanged;
    private readonly EventHandler _onMcpChanged;
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
    private int _activityGeneration;
    private bool _lastHasApiKey = true;
    private string? _testingConnectionId;
    private bool _disposed;

    private HomeModelCard _modelCard = HomeModelCard.Empty;
    private HomeMemoryCard _memoryCard = HomeMemoryCard.Empty;
    private HomeUsageCard _usageCard = HomeUsageCard.Loading;
    private HomeToolsCard _toolsCard = HomeToolsCard.Empty;
    private HomeRemindersCard _remindersCard = HomeRemindersCard.Empty;
    private bool _attentionConnections;
    private bool _attentionSpeech;

    /// <param name="memoryOptions">記憶的上限（筆數、總字數）；沒有時用預設值。</param>
    /// <param name="usage">用量紀錄（費用與預算）；沒有時「本月用量」卡片維持只顯示 tokens。</param>
    public HomeViewModel(IModelCatalog catalog, ConnectionStatusCache status, IUiDispatcher dispatcher,
        IConversationStore conversations, IMcpToolSource mcp, IConnectionTester tester, TimeProvider time,
        MemoryOptions? memoryOptions = null, IUsageStore? usage = null)
    {
        _catalog = catalog;
        _status = status;
        _dispatcher = dispatcher;
        _conversations = conversations;
        _mcp = mcp;
        _tester = tester;
        _time = time;
        _memoryOptions = memoryOptions ?? new MemoryOptions();
        _usage = usage;


        _onCatalogChanged = (_, _) => _dispatcher.Post(OnExternalChange);
        _onStatusChanged = (_, _) => _dispatcher.Post(OnExternalChange);
        _onMcpChanged = (_, _) => _dispatcher.Post(OnExternalChange);
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
        _mcp.Changed += _onMcpChanged;
        Refresh();
    }

    // ---- M21：首頁資料（View 只綁這些）----

    /// <summary>待處理的項目（依序，同一類只出現一條）。沒有項目時是空的，改顯示 <see cref="NoPendingText"/>。</summary>
    public ObservableCollection<HomePendingItem> PendingItems { get; } = [];

    /// <summary>「待處理 · N」。</summary>
    public string PendingTitle => string.Format(CultureInfo.CurrentCulture, Strings.Home_PendingTitleFormat, PendingItems.Count);

    public bool HasPending => PendingItems.Count > 0;

    /// <summary>沒有待處理項目時顯示的一行（「目前沒有需要處理的項目」）。</summary>
    public string NoPendingText => Strings.Home_AllGood;

    public HomeModelCard ModelCard
    {
        get => _modelCard;
        private set => SetProperty(ref _modelCard, value);
    }

    public HomeMemoryCard MemoryCard
    {
        get => _memoryCard;
        private set => SetProperty(ref _memoryCard, value);
    }

    public HomeUsageCard UsageCard
    {
        get => _usageCard;
        private set => SetProperty(ref _usageCard, value);
    }

    public HomeToolsCard ToolsCard
    {
        get => _toolsCard;
        private set => SetProperty(ref _toolsCard, value);
    }

    public HomeRemindersCard RemindersCard
    {
        get => _remindersCard;
        private set => SetProperty(ref _remindersCard, value);
    }

    /// <summary>最近的動作，由新到舊，最多 5 筆（<see cref="LoadActivityAsync"/> 載入）。</summary>
    public ObservableCollection<HomeActionItem> RecentActions { get; } = [];

    public bool HasRecentActions => RecentActions.Count > 0;

    /// <summary>沒有動作紀錄時顯示的一行。</summary>
    public string NoRecentActionsText => Strings.Home_ActionsEmpty;

    /// <summary>AI 連線需要處理（沒有模型、沒有金鑰、連線失敗、尚未測試）：側欄橘點。</summary>
    public bool AttentionConnections
    {
        get => _attentionConnections;
        private set => SetProperty(ref _attentionConnections, value);
    }

    /// <summary>語音辨識尚未設定（端點為空）：側欄橘點。</summary>
    public bool AttentionSpeech
    {
        get => _attentionSpeech;
        private set => SetProperty(ref _attentionSpeech, value);
    }

    /// <summary>就地測試工作台預設模型的連線（用已儲存的金鑰），結果寫進 <see cref="ConnectionStatusCache"/>。測試中停用。</summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        var workbench = ResolveWorkbench(_catalog.Current);
        if (workbench is null)
            return;

        var connection = workbench.Value.Connection;
        _testingConnectionId = connection.Id;
        TestConnectionCommand.NotifyCanExecuteChanged();
        ApplyPending(_catalog.Current, _lastHasApiKey);
        try
        {
            var result = await _tester.TestAsync(connection, null, CancellationToken.None);
            if (!_disposed)
                _status.Set(connection.Id, result);
        }
        finally
        {
            _testingConnectionId = null;
            TestConnectionCommand.NotifyCanExecuteChanged();
            if (!_disposed)
                Refresh();
        }
    }

    private bool CanTestConnection() => _testingConnectionId is null;

    /// <summary>待處理或卡片要求切到設定的某一頁（由 <see cref="SettingsViewModel"/> 接手）。</summary>
    public event Action<SettingsNavigation>? NavigateRequested;

    /// <summary>
    /// 載入「本月／今天的用量」與「最近的動作」。查詢在背景執行，結果經 <see cref="IUiDispatcher"/> 回到 UI 執行緒才套用。
    /// 頁面顯示時呼叫；較晚開始的載入讓較早的結果作廢。
    /// </summary>
    public async Task LoadActivityAsync()
    {
        if (_disposed)
            return;

        var generation = ++_activityGeneration;
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var monthStart = ToUtc(new DateTime(localNow.Year, localNow.Month, 1), zone);
        var todayStart = ToUtc(localNow.Date, zone);
        var budget = _catalog.Current.Usage.MonthlyBudgetUsd;   // 目錄讀取留在呼叫的執行緒上

        try
        {
            var month = await _conversations.GetTokenTotalsAsync(monthStart).ConfigureAwait(false);
            var today = await _conversations.GetTokenTotalsAsync(todayStart).ConfigureAwait(false);
            var actions = await _conversations.ListRecentToolActionsAsync(RecentActionLimit).ConfigureAwait(false);
            var money = _usage is { } usage ? await LoadMoneyAsync(usage, now, zone, budget).ConfigureAwait(false) : null;
            _dispatcher.Post(() => ApplyActivity(generation, now, zone, month, today, actions, money));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _dispatcher.Post(() =>
            {
                if (!_disposed && generation == _activityGeneration)
                    UsageCard = HomeUsageCard.Unavailable;
            });
        }
    }

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
        _mcp.Changed -= _onMcpChanged;
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
        _lastHasApiKey = hasApiKey;
        ApplyModelCard(settings, hasApiKey);
        ApplyMemoryCard();
        ApplyToolsCard(settings);
        ApplyRemindersCard();
        ApplyPending(settings, hasApiKey);
    }

    private static string ConnectionStatusText(bool hasApiKey, ConnectionStatus status) => !hasApiKey ? Strings.Home_NoApiKey : status switch
    {
        ConnectionStatus.Ok => Strings.Home_StatusOk,
        ConnectionStatus.Failed => Strings.Home_StatusFailed,
        _ => Strings.Home_StatusUnknown,
    };

    // ---- M21 首頁資料 ----

    private void ApplyModelCard(AppSettings settings, bool hasApiKey)
    {
        var workbench = ResolveWorkbench(settings);
        if (workbench is null)
        {
            ModelCard = new HomeModelCard(Strings.Home_NoModel, null, null, NeedsAttention: true, ShowSharedNote: false);
            return;
        }

        var (model, connection) = workbench.Value;
        var status = _status.Get(connection.Id).Status;
        ModelCard = new HomeModelCard(
            Name: model.DisplayName,
            DetailText: string.Format(CultureInfo.CurrentCulture, Strings.Home_ModelDetailFormat, connection.Name, model.ModelId),
            StatusText: ConnectionStatusText(hasApiKey, status),
            NeedsAttention: !hasApiKey || status == ConnectionStatus.Failed,
            // 小精靈與工作台都走 ConversationService.ResolveModel，只用 WorkbenchModelId（SpriteModelId 已不使用），所以一定共用
            ShowSharedNote: true,
            ModelId: model.ModelId);
    }

    private void ApplyMemoryCard()
    {
        if (_memoryVm is null)
        {
            MemoryCard = HomeMemoryCard.Empty;
            return;
        }

        var items = _memoryVm.Groups.Sum(g => g.Items.Count);
        var chars = _memoryVm.CharacterCount;
        var maxItems = _memoryOptions.MaxItems;
        var maxChars = _memoryOptions.MaxTotalLength;
        MemoryCard = new HomeMemoryCard(
            StatusText: _memoryVm.IsEnabled ? Strings.Home_MemoryOn : Strings.Home_MemoryOff,
            ItemsText: string.Format(CultureInfo.CurrentCulture, Strings.Home_MemoryItemsFormat, Num(items), Num(maxItems)),
            ItemsProgress: Ratio(items, maxItems),
            CharsText: string.Format(CultureInfo.CurrentCulture, Strings.Home_MemoryCharsFormat, Num(chars), Num(maxChars)),
            CharsProgress: Ratio(chars, maxChars));
    }

    private void ApplyToolsCard(AppSettings settings)
    {
        // 內建工具：群組代號不是 mcp: 開頭的
        var builtIn = _toolsVm is null
            ? new List<ToolItemViewModel>()
            : _toolsVm.Groups.Where(g => !g.Id.StartsWith(McpGroupPrefix, StringComparison.Ordinal)).SelectMany(g => g.Items).ToList();
        var builtInEnabled = builtIn.Count(i => i.IsEnabled);

        // MCP：只看已啟用的伺服器（與工具清單的分組相同），依名稱排序
        var statuses = _mcp.GetStatuses()
            .GroupBy(s => s.ServerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = settings.McpServers
            .Where(s => s.Enabled)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s =>
            {
                statuses.TryGetValue(s.Id, out var status);
                var ready = status?.State == McpServerState.Ready;
                var serverId = s.Id;
                return new HomeMcpServerItem(
                    ServerId: serverId,
                    Name: s.Name,
                    IsConnected: ready,
                    StatusText: ready
                        ? string.Format(CultureInfo.CurrentCulture, Strings.Home_McpToolCountFormat, status!.ToolCount)
                        : Strings.Home_McpOffline,
                    CanReconnect: status?.State == McpServerState.Failed,
                    ReconnectCommand: new AsyncRelayCommand(() => _mcp.RestartAsync(serverId, CancellationToken.None)));
            })
            .ToList();

        var connected = rows.Count(r => r.IsConnected);
        ToolsCard = new HomeToolsCard(
            BuiltInEnabled: builtInEnabled,
            BuiltInTotal: builtIn.Count,
            BuiltInText: string.Format(CultureInfo.CurrentCulture, Strings.Home_ToolsEnabledFormat, builtInEnabled, builtIn.Count),
            McpConnected: connected,
            McpTotal: rows.Count,
            McpText: string.Format(CultureInfo.CurrentCulture, Strings.Home_ToolsMcpConnectedFormat, connected, rows.Count),
            McpServers: rows.Take(McpRowLimit).ToList(),
            MoreText: rows.Count > McpRowLimit
                ? string.Format(CultureInfo.CurrentCulture, Strings.Home_McpMoreFormat, rows.Count - McpRowLimit)
                : null);
    }

    private void ApplyRemindersCard()
    {
        if (_remindersVm is null)
        {
            RemindersCard = HomeRemindersCard.Empty;
            return;
        }

        var rows = _remindersVm.Pending;
        var timers = rows.Count(r => r.Item.Kind == ScheduledKind.Timer);
        var next = rows.FirstOrDefault(r => r.Item.Kind != ScheduledKind.Timer && r.Item.Enabled);
        string nextText;
        if (next is null)
        {
            nextText = Strings.Home_RemindersNoneHint;
        }
        else
        {
            var when = string.IsNullOrEmpty(next.ScheduleText) ? next.Title : next.Title + " · " + next.ScheduleText;
            nextText = string.Format(CultureInfo.CurrentCulture, Strings.Home_RemindersNextFormat, when);
        }

        RemindersCard = new HomeRemindersCard(
            PendingCount: rows.Count(r => r.Item.Kind != ScheduledKind.Timer),
            TimerCount: timers,
            NextText: nextText,
            HasNext: next is not null);
    }

    private enum PendingKind { NoModel, NoKey, Failed, Untested, Speech }

    /// <summary>
    /// 待處理規則（依序，同一類只出現一條）：沒有模型 → 前往設定；沒有金鑰 → 前往設定（金鑰欄位）；
    /// 連線失敗 → 就地重新測試；尚未測試 → 就地測試連線；語音端點為空 → 前往設定。
    /// </summary>
    private void ApplyPending(AppSettings settings, bool hasApiKey)
    {
        var items = new List<HomePendingItem>();
        var connectionsAttention = false;

        var workbench = ResolveWorkbench(settings);
        if (workbench is null)
        {
            connectionsAttention = true;
            items.Add(Item(PendingKind.NoModel, "LumiIconDiamond", Strings.Home_PendingNoModelTitle, Strings.Home_PendingNoModelDesc,
                Strings.Home_PendingGoSettings, isPrimary: false,
                GoTo(new SettingsNavigation(SettingsSection.Models))));
        }
        else
        {
            var connection = workbench.Value.Connection;
            var status = _status.Get(connection.Id).Status;
            var testing = _testingConnectionId == connection.Id;
            if (!hasApiKey)
            {
                connectionsAttention = true;
                items.Add(Item(PendingKind.NoKey, "LumiIconDiamond", Strings.Home_PendingNoKeyTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.Home_PendingNoKeyDescFormat, connection.Name),
                    Strings.Home_PendingGoSettings, isPrimary: false,
                    GoTo(new SettingsNavigation(SettingsSection.Connections, connection.Id, FocusApiKey: true))));
            }
            else if (status == ConnectionStatus.Failed)
            {
                connectionsAttention = true;
                items.Add(Item(PendingKind.Failed, "LumiIconDiamond", Strings.Home_PendingFailedTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.Home_PendingFailedDescFormat, connection.Name),
                    testing ? Strings.Home_PendingTesting : Strings.Home_PendingRetest, isPrimary: true,
                    TestConnectionCommand));
            }
            else if (status == ConnectionStatus.Unknown)
            {
                connectionsAttention = true;
                items.Add(Item(PendingKind.Untested, "LumiIconDiamond", Strings.Home_PendingUntestedTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.Home_PendingUntestedDescFormat, connection.Name),
                    testing ? Strings.Home_PendingTesting : Strings.Home_PendingTest, isPrimary: true,
                    TestConnectionCommand));
            }
        }

        var speechMissing = string.IsNullOrWhiteSpace(settings.Speech.Endpoint);
        if (speechMissing)
        {
            items.Add(Item(PendingKind.Speech, "LumiIconMic", Strings.Home_PendingSpeechTitle, Strings.Home_PendingSpeechDesc,
                Strings.Home_PendingGoSettings, isPrimary: false,
                GoTo(new SettingsNavigation(SettingsSection.Speech))));
        }

        PendingItems.Clear();
        foreach (var item in items)
            PendingItems.Add(item);
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingTitle));
        AttentionConnections = connectionsAttention;
        AttentionSpeech = speechMissing;
    }

    private static HomePendingItem Item(PendingKind kind, string iconKey, string title, string description, string actionText,
        bool isPrimary, ICommand command) =>
        new(kind.ToString(), iconKey, title, description, actionText, isPrimary, command);

    private ICommand GoTo(SettingsNavigation navigation) => new RelayCommand(() => NavigateRequested?.Invoke(navigation));

    /// <summary>本月的費用月報，以及今天有沒有估算值（有的話今天的金額前加「≈」）。</summary>
    private sealed record MoneySnapshot(MonthlyUsageSummary Month, bool TodayEstimated);

    private static async Task<MoneySnapshot> LoadMoneyAsync(IUsageStore usage, DateTimeOffset now, TimeZoneInfo zone, decimal? budget)
    {
        var (from, to) = MonthlyUsageSummary.MonthRange(now, zone);
        var records = await usage.ListAsync(from, to).ConfigureAwait(false);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var todayEstimated = records.Any(r => r.Estimated && TimeZoneInfo.ConvertTime(r.CreatedAt, zone).Date == today);
        return new MoneySnapshot(MonthlyUsageSummary.Build(records, budget, now, zone), todayEstimated);
    }

    private void ApplyActivity(int generation, DateTimeOffset now, TimeZoneInfo zone, TokenTotals month, TokenTotals today,
        IReadOnlyList<RecentToolAction> actions, MoneySnapshot? money)
    {
        if (_disposed || generation != _activityGeneration)
            return;

        UsageCard = money is null ? TokenUsageCard(month, today) : MoneyUsageCard(money, month);

        RecentActions.Clear();
        foreach (var action in actions.Take(RecentActionLimit))
            RecentActions.Add(ToActionItem(action, now, zone));
        OnPropertyChanged(nameof(HasRecentActions));
    }

    /// <summary>沒有用量紀錄（<see cref="IUsageStore"/> 為 null）時：大數字是 tokens，與改版前相同。</summary>
    private static HomeUsageCard TokenUsageCard(TokenTotals month, TokenTotals today) => new(
        IsLoading: false,
        IsUnavailable: false,
        MonthText: WorkbenchViewModel.FormatTokens(ToInt(month.Total)),
        TodayText: string.Format(CultureInfo.CurrentCulture, Strings.Home_UsageTodayFormat, WorkbenchViewModel.FormatTokens(ToInt(today.Total))),
        InputText: string.Format(CultureInfo.CurrentCulture, Strings.Home_UsageInputFormat, WorkbenchViewModel.FormatTokens(ToInt(month.Input))),
        OutputText: string.Format(CultureInfo.CurrentCulture, Strings.Home_UsageOutputFormat, WorkbenchViewModel.FormatTokens(ToInt(month.Output))),
        ShowTokensUnit: true);

    /// <summary>
    /// 有用量紀錄時：大數字是本月新台幣金額，今天的金額，有預算時加預算列與進度條（等級規則同用量頁：&lt; 80% 正常、80–99% 警告、≥ 100% 危險）。
    /// 輸入／輸出的 tokens 小標籤照舊。
    /// </summary>
    private static HomeUsageCard MoneyUsageCard(MoneySnapshot money, TokenTotals month)
    {
        var summary = money.Month;
        var todayUsd = summary.Daily.Single(d => d.IsToday).Usd;

        string? budgetText = null;
        double? ratio = null;
        var level = UsageLevel.Normal;
        if (summary.BudgetUsd is { } limit)
        {
            // 百分比無條件捨去：到 100% 才顯示 100%，和「≥ 100% 危險」的判斷一致
            var percent = (int)Math.Floor(summary.TotalUsd / limit * 100);
            budgetText = string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_BudgetFormat, MoneyFormat.Twd(limit), percent);
            ratio = Math.Min((double)(summary.TotalUsd / limit), 1d);
            level = summary.TotalUsd >= limit ? UsageLevel.Danger
                : summary.TotalUsd >= limit * 0.8m ? UsageLevel.Warning
                : UsageLevel.Normal;
        }

        return new HomeUsageCard(
            IsLoading: false,
            IsUnavailable: false,
            MonthText: MoneyFormat.Twd(summary.TotalUsd, summary.HasEstimated),
            TodayText: string.Format(CultureInfo.CurrentCulture, Strings.Home_UsageTodayFormat, MoneyFormat.Twd(todayUsd, money.TodayEstimated)),
            InputText: string.Format(CultureInfo.CurrentCulture, Strings.Home_UsageInputFormat, WorkbenchViewModel.FormatTokens(ToInt(month.Input))),
            OutputText: string.Format(CultureInfo.CurrentCulture, Strings.Home_UsageOutputFormat, WorkbenchViewModel.FormatTokens(ToInt(month.Output))),
            BudgetText: budgetText,
            BudgetRatio: ratio,
            BudgetLevel: level,
            ShowTokensUnit: false);
    }

    private static HomeActionItem ToActionItem(RecentToolAction action, DateTimeOffset now, TimeZoneInfo zone)
    {
        var result = action.Result;
        var status = result.Approval == ToolApproval.Denied ? HomeActionStatus.Denied
            : result.IsError ? HomeActionStatus.Failed
            : HomeActionStatus.Succeeded;
        var badge = result.Approval switch
        {
            ToolApproval.AllowedOnce or ToolApproval.AllowedForConversation or ToolApproval.AllowedAlways => Strings.Home_ActionAllowed,
            ToolApproval.Denied => Strings.Home_ActionDenied,
            _ => null,
        };
        return new HomeActionItem(RelativeTime(action.At, now, zone), status, result.DisplayName, result.Target, result.Summary, badge);
    }

    /// <summary>剛剛／N 分鐘前／N 小時前（同一天）／昨天／更早顯示 M/d（本地日期）。</summary>
    private static string RelativeTime(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var diff = now - at;
        if (diff < TimeSpan.FromMinutes(1))
            return Strings.Home_TimeNow;
        if (diff < TimeSpan.FromHours(1))
            return string.Format(CultureInfo.CurrentCulture, Strings.Home_TimeMinutesFormat, (int)diff.TotalMinutes);

        var localAt = TimeZoneInfo.ConvertTime(at, zone).Date;
        var localNow = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (localAt == localNow)
            return string.Format(CultureInfo.CurrentCulture, Strings.Home_TimeHoursFormat, (int)diff.TotalHours);
        if (localAt == localNow.AddDays(-1))
            return Strings.Home_TimeYesterday;
        return $"{localAt.Month}/{localAt.Day}";
    }

    /// <summary>本地時區的日期起點（00:00）換成 UTC。</summary>
    private static DateTimeOffset ToUtc(DateTime localMidnight, TimeZoneInfo zone) =>
        new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight)).ToUniversalTime();

    private static int ToInt(long value) => (int)Math.Min(value, int.MaxValue);

    private static string Num(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static double Ratio(long value, int max) => max <= 0 ? 0 : Math.Clamp((double)value / max, 0, 1);
}
