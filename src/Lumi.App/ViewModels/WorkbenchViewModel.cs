using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Speech;
using Lumi.Core.Tools;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台主視窗。對話與回應流程在 <see cref="IChatSession"/>（和小精靈共用），這裡是它的觀察者：
/// 收到事件就更新訊息列表。命令都在 UI 執行緒上執行，會話的事件也在呼叫者的同步內容上發出，
/// 所以直接更新畫面，不需要另外轉送（agent.md 第 12 節）。
/// </summary>
public partial class WorkbenchViewModel : ObservableObject, IHistoryHost
{
    private readonly IConversationStore _store;
    private readonly IModelCatalog _catalog;
    private readonly IApiKeyPrompt _apiKeyPrompt;
    private readonly ISettingsWindowService _settingsWindow;
    private readonly ConnectionStatusCache _status;
    private readonly IClipboardService _clipboard;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkbenchViewModel> _logger;
    private readonly IToolApprovalResponder _approvals;

    private readonly IChatSession _session;
    private readonly IAppModeCoordinator? _modes;
    private bool _initialized;

    // 目前這一輪回應的畫面狀態（RunStarted 建立、RunFinished 清掉）
    private MessageViewModel? _bubble;
    private readonly Dictionary<string, ToolCardViewModel> _cards = new(StringComparer.Ordinal);
    private bool _usedTools;
    private string? _runConnectionId;

    /// <summary>這一輪是工作台自己送出的（失敗後的金鑰提示、重送由它處理）；小精靈送出的只更新畫面。</summary>
    private bool _ownRun;

    /// <summary>工作台自己切換對話時，會話發出的 <see cref="IChatSession.ConversationChanged"/> 不用再處理一次。</summary>
    private bool _navigating;

    private string? CurrentConversationId => _session.ConversationId;

    public WorkbenchViewModel(
        ConversationService conversations,
        IConversationStore store,
        IModelCatalog catalog,
        IApiKeyPrompt apiKeyPrompt,
        ISettingsWindowService settingsWindow,
        ConnectionStatusCache status,
        IClipboardService clipboard,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<WorkbenchViewModel> logger,
        IToolApprovalResponder? approvals = null,
        ContextBarViewModel? contextBar = null,
        IChatSession? session = null,
        IAppModeCoordinator? modes = null,
        IVoiceInputController? voice = null,
        ConversationDeletionService? deletion = null,
        IConfirmService? confirm = null,
        ILogger<HistoryViewModel>? historyLogger = null,
        NotificationCenter? notifications = null,
        IFilePickerService? filePicker = null,
        IRenameService? rename = null,
        IConnectionTester? tester = null,
        SpriteConversationService? spriteConversation = null,
        UpdateNoticeViewModel? update = null,
        IUsageStore? usageStore = null,
        BudgetMonitor? budgetMonitor = null)
    {
        _store = store;
        _catalog = catalog;
        _apiKeyPrompt = apiKeyPrompt;
        _settingsWindow = settingsWindow;
        _status = status;
        _clipboard = clipboard;
        _time = time;
        _logger = logger;
        _filePicker = filePicker;
        _rename = rename;
        _tester = tester;
        _spriteConversation = spriteConversation;
        _usageStore = usageStore;
        _budget = budgetMonitor;
        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowWelcome));
            NotifyConversationActions();
        };
        _approvals = approvals ?? NullToolApprovalResponder.Instance;
        _session = session ?? new ChatSession(conversations, store, NullLogger<ChatSession>.Instance);
        _modes = modes;
        Voice = voice ?? NoVoiceInputController.Instance;
        Voice.PropertyChanged += OnVoicePropertyChanged;
        Voice.Transcribed += OnVoiceTranscribed;
        Context = contextBar ?? ContextBarViewModel.CreateInert(store, time);
        _notice = catalog.LoadProblem;

        History = new HistoryViewModel(store, this, time, HistoryOptions.Default, deletion, confirm, historyLogger, spriteConversation);
        ModelSwitcher = new ModelSwitcherViewModel(catalog, status, settingsWindow, dispatcher, () => IsResponding);
        ModelSwitcher.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelSwitcherViewModel.CapsuleTooltip))
                OnPropertyChanged(nameof(ModelCapsuleTip));
        };
        // 錯誤列的「換模型重試」：選完模型接著重送
        RetrySwitcher = new ModelSwitcherViewModel(catalog, status, settingsWindow, dispatcher, () => IsResponding, RetryAfterModelSwitchAsync);
        Notifications = notifications is null
            ? new NotificationStripViewModel(new ObservableCollection<NotificationItemViewModel>(), () => { })
            : new NotificationStripViewModel(notifications.Items, notifications.DismissAll);
        Update = update ?? UpdateNoticeViewModel.Inert;
        Usage = new ConversationUsageViewModel(RetryConnectionAsync, OpenUsagePage);
        // 費用段落只在有用量資料來源時顯示（沒有就和費用功能出現之前一樣）
        Usage.ShowCost = usageStore is not null && budgetMonitor is not null;
        BudgetAlert = new BudgetAlertViewModel(OpenUsagePage);
        RefreshUsage();

        _session.ConversationChanged += OnConversationChanged;
        _session.RunStarted += OnRunStarted;
        _session.EventReceived += OnEventReceived;
        _session.RunFinished += OnRunFinished;
        _session.ModeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsViewOnly));
            EnterSpriteModeCommand.NotifyCanExecuteChanged();
        };
        _approvals.Resolved += OnApprovalResolved;

        _showTokenUsage = catalog.Current.Display.ShowTokenUsage;
        _isTopmost = catalog.Current.Behavior.WorkbenchTopmost;
        catalog.Changed += (_, settings) => dispatcher.Post(() =>
        {
            ShowTokenUsage = settings.Display.ShowTokenUsage;
            IsTopmost = settings.Behavior.WorkbenchTopmost;
            OnModelSettingsChanged();
        });
        status.Changed += (_, _) => dispatcher.Post(OnConnectionStatusChanged);
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    /// <summary>
    /// 歡迎區（「今天想請 Lumi 幫什麼忙？」）：對話沒有任何訊息、而且不是還在載入最近一則對話時才顯示，
    /// 避免啟動時載入前閃一下。
    /// </summary>
    public bool ShowWelcome => !IsLoadingMessages && Messages.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    private bool _isLoadingMessages;

    public HistoryViewModel History { get; }

    /// <summary>輸入框上方的上下文列：這則對話的檔案、資料夾、圖片。</summary>
    public ContextBarViewModel Context { get; }

    public ModelSwitcherViewModel ModelSwitcher { get; }

    public ModelSwitcherViewModel RetrySwitcher { get; }

    /// <summary>工作台頂端的提醒通知列（M10 10E）；沒有送達器時是空的。</summary>
    public NotificationStripViewModel Notifications { get; }

    /// <summary>有新版的提醒（工作台頂端）。</summary>
    public UpdateNoticeViewModel Update { get; }

    /// <summary>工作台視窗釘選在最上層（標題列的圖釘；存在設定，下次開啟仍是這個狀態）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopmostTip))]
    private bool _isTopmost;

    public string TopmostTip => IsTopmost ? Strings.Workbench_TopmostUnpin : Strings.Workbench_TopmostPin;

    [RelayCommand]
    private async Task ToggleTopmostAsync()
    {
        var wanted = !IsTopmost;
        IsTopmost = wanted;   // 先讓視窗動起來；存不起來再還原
        try
        {
            await _catalog.SetBehaviorAsync(_catalog.Current.Behavior with { WorkbenchTopmost = wanted });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            IsTopmost = _catalog.Current.Behavior.WorkbenchTopmost;
            Notice = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
        }
    }

    private ResolvedModel? WorkbenchModel => _catalog.Resolve(_catalog.Current.WorkbenchModelId);

    public string ModelName => WorkbenchModel is { } m ? m.Model.DisplayName : Strings.Workbench_NoModel;

    public int ContextWindow => WorkbenchModel?.Model.ContextWindow ?? 0;

    /// <summary>一次性的提示（例如設定檔損毀已改用預設）；按「知道了」關掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => Notice is not null;

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    /// <summary>開啟設定視窗（不等它關閉；視窗是獨立的，可以和工作台並排）。</summary>
    [RelayCommand]
    private void OpenSettings() => _ = _settingsWindow.ShowAsync(new SettingsNavigation(SettingsSection.Home));

    private void OnModelSettingsChanged()
    {
        Context.ContextWindow = ContextWindow;
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ContextWindow));
        OnPropertyChanged(nameof(ContextText));
        RefreshUsage();
        OnConnectionStatusChanged();
    }

    // ---- 連線狀態（模型選單的「本對話」區塊與膠囊 Tooltip 另用 ModelSwitcher 的同一份狀態） ----

    private ConnectionStatus CurrentConnectionStatus =>
        WorkbenchModel is { } m ? _status.Get(m.Connection.Id).Status : ConnectionStatus.Unknown;

    public bool IsConnectionOk => CurrentConnectionStatus == ConnectionStatus.Ok;

    public bool IsConnectionFailed => CurrentConnectionStatus == ConnectionStatus.Failed;

    public bool IsConnectionUnknown => CurrentConnectionStatus == ConnectionStatus.Unknown;

    public string ConnectionStatusText => CurrentConnectionStatus switch
    {
        ConnectionStatus.Ok => Strings.Workbench_StatusConnected,
        ConnectionStatus.Failed => Strings.Workbench_StatusFailed,
        _ => Strings.Workbench_StatusUnknown,
    };

    private void OnConnectionStatusChanged()
    {
        OnPropertyChanged(nameof(IsConnectionOk));
        OnPropertyChanged(nameof(IsConnectionFailed));
        OnPropertyChanged(nameof(IsConnectionUnknown));
        OnPropertyChanged(nameof(ConnectionStatusText));
        RefreshUsage();
    }

    [ObservableProperty]
    private string _title = Strings.Workbench_Title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSwitchBlockedHint), nameof(CanSwitchModel), nameof(IsViewOnly), nameof(SpriteModeTip), nameof(ModelCapsuleTip), nameof(AddFileTip), nameof(AddFolderTip), nameof(CanCompactAndContinue))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(RetryCommand), nameof(RegenerateCommand), nameof(EnterSpriteModeCommand), nameof(CompactAndContinueCommand))]
    private bool _isResponding;

    /// <summary>
    /// 「查看中」（ui-spec 2.2）：小精靈正在回應時開啟的工作台，輸入、模型切換、模式切換都停用，頂端顯示說明；
    /// 可以捲動、展開卡片、複製內容，權限確認在任一邊回答都會同步。回應結束自動解除。
    /// 它不是獨立的狀態，就是「回應中，而且作用中的模式是小精靈」。
    /// </summary>
    public bool IsViewOnly => IsResponding && _session.ActiveMode == ChatMode.Sprite && _modes is not null;

    /// <summary>「小精靈模式」按鈕：隱藏工作台、顯示小精靈。回應中不能切換。</summary>
    [RelayCommand(CanExecute = nameof(CanEnterSpriteMode))]
    private void EnterSpriteMode() => _modes?.EnterSpriteMode();

    private bool CanEnterSpriteMode() => _modes is not null && !IsResponding;

    public bool HasModeCoordinator => _modes is not null;

    partial void OnIsRespondingChanged(bool value)
    {
        RaiseVoiceProperties();
        NotifyConversationActions();
        History.HostStateChanged();
    }

    partial void OnInputTextChanged(string value)
    {
        Context.UpdateDraft(value);
        // 使用者改了語音轉寫的文字（或清空）就拿掉「語音轉寫」標籤；提示也在下一次輸入時消失
        if (IsVoiceDraft && value != _voiceDraftText)
            IsVoiceDraft = false;
        VoiceNotice = null;
    }

    /// <summary>回應中，膠囊與小精靈按鈕停用並顯示「回應中，暫時無法切換」（ui-spec 2.2）。</summary>
    public bool ShowSwitchBlockedHint => IsResponding;

    public bool CanSwitchModel => !IsResponding;

    /// <summary>模型膠囊的 Tooltip：平時是供應商、model ID、連線狀態與費用說明；回應中膠囊停用，改說明為什麼（取代原本佔版面的提示文字）。</summary>
    public string ModelCapsuleTip => IsResponding ? Strings.Workbench_SwitchBlocked : ModelSwitcher.CapsuleTooltip;

    /// <summary>「小精靈模式」按鈕的 Tooltip：回應中說明為什麼停用，平時是按鈕名稱（窄版只剩圖示時靠它說明）。</summary>
    public string SpriteModeTip => IsResponding ? Strings.Workbench_SwitchBlocked : Strings.Workbench_SpriteMode;

    /// <summary>「加入檔案」「加入資料夾」的 Tooltip：回應中停用，改說明為什麼（取代原本佔版面的提示文字）。</summary>
    public string AddFileTip => AddBlockedTip ?? Strings.Context_AddFile;

    public string AddFolderTip => AddBlockedTip ?? Strings.Context_AddFolder;

    /// <summary>加入按鈕停用時的原因（回應中、語音進行中）；可以加入時是 null。</summary>
    private string? AddBlockedTip => IsResponding ? Strings.Composer_AddBlocked : IsVoiceActive ? Strings.Composer_VoiceBlocked : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextUsed), nameof(ContextText))]
    private TokenUsage? _lastUsage;

    /// <summary>設定 › 外觀與操作 › 進階：在每則回答下方顯示輸入／輸出 token 數（預設關閉）。</summary>
    [ObservableProperty]
    private bool _showTokenUsage;

    [ObservableProperty]
    private int _turns;

    partial void OnLastUsageChanged(TokenUsage? value) => RefreshUsage();

    partial void OnTurnsChanged(int value) => RefreshUsage();

    public bool HasError => ErrorMessage is not null;

    private int InputTokensTotal => LastUsage is { } u ? u.InputTokens + u.CacheCreationInputTokens + u.CacheReadInputTokens : 0;

    /// <summary>最後一次回應用掉的上下文：輸入（含快取）＋輸出。</summary>
    public int ContextUsed => InputTokensTotal + (LastUsage?.OutputTokens ?? 0);

    public string ContextText => ContextWindow > 0
        ? $"{FormatTokens(ContextUsed)} / {FormatTokens(ContextWindow)}"
        : FormatTokens(ContextUsed);

    /// <summary>啟動時載入最近一則對話；沒有就建一則空的。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;   // 啟動時就載入（小精靈模式下工作台視窗還沒開），視窗第一次開啟時不要再載入一次
        _initialized = true;
        IsLoadingMessages = true;
        try
        {
            var conversation = await NavigateAsync(() => _session.InitializeAsync(Strings.Workbench_NewConversationTitle));
            await LoadAsync(conversation);
        }
        finally
        {
            IsLoadingMessages = false;
        }
    }

    // ---- 送出、重試、重新產生、停止 ----

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        var conversationId = CurrentConversationId!;
        _modes?.WorkbenchWillSend();   // 在工作台送出＝切換到工作台模式（小精靈隱藏）
        InputText = "";
        if (Messages.Count == 0)
            Title = ConversationService.MakeTitle(text);   // 與 ConversationService 存進資料庫的標題相同
        // 待送出的項目這次會帶進訊息：氣泡上先顯示它們的 chip
        Messages.Add(MessageViewModel.User(text, MessageChipViewModel.FromChips(Context.PendingChips()), _time.GetUtcNow()));
        await RunResponseAsync(() => _session.SendAsync(text), reloadOnFailure: false);
        if (conversationId == CurrentConversationId)
            await Context.RefreshAsync();   // 項目已經變成「已帶入」
    }

    private bool CanSend() => !IsResponding && !IsCompacting && !IsVoiceActive && CurrentConversationId is not null && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync()
    {
        _modes?.WorkbenchWillSend();
        return RunResponseAsync(() => _session.RetryAsync(), reloadOnFailure: false);
    }

    private bool CanRetry() => !IsResponding && !IsCompacting && HasError;

    /// <summary>「換模型重試」：模型已經切好了，接著重送（只有最後一則是使用者訊息時才有東西可重送）。</summary>
    private Task RetryAfterModelSwitchAsync() => RetryCommand.CanExecute(null) ? RetryAsync() : Task.CompletedTask;

    /// <summary>重新產生：移除最後一則助理訊息，用同一段歷史再要一次；新回應取代舊的。</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private Task RegenerateAsync()
    {
        _modes?.WorkbenchWillSend();
        ErrorMessage = null;
        return RunResponseAsync(() => _session.RegenerateAsync(), reloadOnFailure: true);
    }

    private bool CanRegenerate() => !IsResponding && !IsCompacting && CurrentConversationId is not null && Messages.LastOrDefault() is { IsUser: false };

    [RelayCommand(CanExecute = nameof(IsResponding))]
    private void Stop() => _session.Stop();

    /// <summary>取消目前的回應，並等它的收尾全部做完（存「已停止」、移除空泡泡）才回來。沒有回應進行中就直接回來。</summary>
    public Task StopAndWaitAsync() => _session.StopAndWaitAsync();

    // ---- 對話：新增、切換 ----

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        // 目前這則還沒有任何訊息：已經是空白的了，不重複建立（小精靈的專屬對話不算：它要留給小精靈）
        if (Messages.Count == 0 && CurrentConversationId is not null && !IsSpriteConversation)
            return;

        await CreateNewConversationAsync();
    }

    /// <summary>建立一則空白對話並切過去（不列在歷史裡，直到有訊息為止）。</summary>
    private async Task CreateNewConversationAsync()
    {
        var conversation = await NavigateAsync(() => _session.NewConversationAsync(Strings.Workbench_NewConversationTitle));
        await LoadAsync(conversation);
        await History.RefreshAsync(CurrentConversationId);
    }

    /// <summary>從歷史抽屜切換對話。回應中會先停止（ui-spec 2.2）。</summary>
    public async Task OpenConversationAsync(Conversation conversation)
    {
        if (conversation.Id == CurrentConversationId)
            return;

        await NavigateAsync(() => _session.OpenConversationAsync(conversation));
        await LoadAsync(conversation);
        await History.RefreshAsync(CurrentConversationId);
    }

    // ---- 歷史抽屜的宿主（IHistoryHost）----

    string? IHistoryHost.CurrentConversationId => CurrentConversationId;

    bool IHistoryHost.IsResponding => IsResponding;

    Task IHistoryHost.OpenAsync(Conversation conversation) => OpenConversationAsync(conversation);

    /// <summary>
    /// 目前開著的對話被刪了：先切到下一則（沒有就開新的空白對話），草稿跟著清掉。
    /// 切換之後 session 已經不指向被刪的對話，不會再對它送出或存檔。
    /// </summary>
    async Task IHistoryHost.ConversationDeletedAsync(string conversationId, Conversation? next)
    {
        if (conversationId != CurrentConversationId)
            return;
        if (next is not null)
            await OpenConversationAsync(next);
        else
            await CreateNewConversationAsync();
        InputText = "";
    }

    void IHistoryHost.TitleChanged(string conversationId, string title)
    {
        if (conversationId == CurrentConversationId)
            Title = title;
    }

    private async Task<Conversation> NavigateAsync(Func<Task<Conversation>> navigate)
    {
        _navigating = true;
        try
        {
            return await navigate();
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>別處（例如小精靈的右鍵選單）切換了對話：工作台跟著顯示。</summary>
    private void OnConversationChanged(object? sender, ConversationChangedArgs args)
    {
        if (!_navigating)
            _ = ShowConversationAsync(args.Conversation);
    }

    private async Task ShowConversationAsync(Conversation conversation)
    {
        try
        {
            await LoadAsync(conversation);
            await History.RefreshAsync(CurrentConversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切換到對話 {ConversationId} 時失敗", conversation.Id);
        }
    }

    private async Task LoadAsync(Conversation conversation)
    {
        Title = conversation.Title;
        ErrorMessage = null;
        await ReloadMessagesAsync();
        await RefreshCostAsync();
        NotifySpriteConversation();   // 小精靈的專屬對話：「⋯」換成「清除內容」（M22）
    }

    /// <summary>從資料庫重讀目前對話的訊息（讓畫面和資料庫一致）。</summary>
    private async Task ReloadMessagesAsync()
    {
        var messages = await _store.GetMessagesAsync(CurrentConversationId!);
        var compaction = await _store.GetCompactionStateAsync(CurrentConversationId!);
        Messages.Clear();
        foreach (var item in ConversationTimeline.Build(messages, use => NewCard(use.Id, use.Name, use.Name, null, null, use.InputJson), compaction))
            Messages.Add(item);
        HasCompactionFailure = false;

        LoadUsageTally(messages, await _store.GetCompactionUsageAsync(CurrentConversationId!));
        var lastWithUsage = messages.LastOrDefault(m => m.Usage is not null);
        LastUsage = lastWithUsage?.Usage;
        // 整理過、而且還沒有整理之後的回應：舊的用量不代表現在送出的大小，改用估算，用量圓環整理完就變小（M22）
        if (!compaction.IsEmpty && (lastWithUsage is null || lastWithUsage.Seq <= compaction.AppliedAtSeq))
            LastUsage = await EstimateCompactedUsageAsync(CurrentConversationId!, messages, compaction);
        RefreshMessageState();
        SendCommand.NotifyCanExecuteChanged();
        Context.ContextWindow = ContextWindow;
        await Context.LoadAsync(CurrentConversationId);
    }

    private ToolCardViewModel NewCard(string callId, string toolName, string displayName, ToolPermission? permission, string? target, string argumentsJson) =>
        new(callId, toolName, displayName, permission, target, argumentsJson, _approvals, _clipboard, _time);

    /// <summary>回合數、「最後一則助理訊息」旗標（只有它有重新產生）。</summary>
    private void RefreshMessageState()
    {
        // 回合數＝有回應的使用者訊息數（一輪可能有好幾個氣泡與工具段落）
        var turns = 0;
        var answered = true;
        foreach (var item in Messages)
        {
            if (item is CompactionDividerViewModel)
                continue;   // 整理的分隔線不是回應
            if (item.IsUser)
                answered = false;
            else if (!answered && !item.IsStreaming)
            {
                turns++;
                answered = true;
            }
        }
        Turns = turns;

        var last = Messages.LastOrDefault();
        for (var i = 0; i < Messages.Count; i++)
        {
            var message = Messages[i];
            message.IsLastAssistant = message.IsAssistant && ReferenceEquals(message, last);
            message.IsFollowedByTools = i + 1 < Messages.Count && Messages[i + 1] is ToolRunViewModel;
        }
        RegenerateCommand.NotifyCanExecuteChanged();
    }

    // ---- 語音輸入（6D）：控制器不碰畫面，這裡把它的狀態翻成輸入區要顯示的東西 ----

    /// <summary>語音輸入控制器（工作台與小精靈共用的單例）。</summary>
    public IVoiceInputController Voice { get; }

    public VoiceInputState VoiceState => Voice.State;

    /// <summary>錄音、轉寫、失敗時收音面板取代輸入框（ui-spec 6.3）。</summary>
    public bool IsVoiceActive => Voice.State != VoiceInputState.Idle;

    public bool IsVoiceRecording => Voice.State == VoiceInputState.Recording;

    public bool IsVoiceTranscribing => Voice.State == VoiceInputState.Transcribing;

    public bool IsVoiceFailed => Voice.State == VoiceInputState.Failed;

    /// <summary>麥克風鈕能不能按（沒有原因就能）。</summary>
    public bool VoiceCanStart => Voice.CanStart;

    /// <summary>麥克風鈕的 Tooltip：能用時是按鈕名稱，不能用時是原因（ShowOnDisabled）。</summary>
    public string MicTip => Voice.DisabledReasonFor(VoiceOrigin.Workbench) ?? Strings.Composer_Mic;

    /// <summary>送出鈕與提示文字：語音進行中隱藏（版面不動，不會誤送）。</summary>
    public bool ShowSendButton => !IsResponding && !IsVoiceActive;

    public bool ShowSendHint => ShowSendButton;

    /// <summary>收音面板的計時：「01:12 / 01:30」。</summary>
    public string VoiceTimeText => string.Format(CultureInfo.CurrentCulture, Strings.Voice_Time, FormatClock(Voice.Elapsed), FormatClock(RecordingLimits.MaxDuration));

    /// <summary>目前狀態的文字（收音中 00:12、正在辨識語音…、失敗標題）；也當作收音面板的無障礙名稱。</summary>
    public string VoiceStatusText => Voice.State switch
    {
        VoiceInputState.Recording => string.Format(CultureInfo.CurrentCulture, Strings.Voice_StatusRecording, FormatClock(Voice.Elapsed)),
        VoiceInputState.Transcribing => string.Format(CultureInfo.CurrentCulture, Strings.Voice_StatusTranscribing, FormatClock(Voice.RecordedDuration ?? TimeSpan.Zero)),
        VoiceInputState.Failed => Strings.Voice_FailedTitle,
        _ => "",
    };

    public double VoiceLevel => Voice.Level;

    public string VoiceFailureText => Voice.LastFailureMessage ?? "";

    /// <summary>一次性的語音提示（太短、沒聽到內容、對話已改變）；下次輸入或開始錄音時消失。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVoiceNotice))]
    private string? _voiceNotice;

    public bool HasVoiceNotice => VoiceNotice is not null;

    /// <summary>輸入框的文字是語音轉寫來的：上方顯示「語音轉寫」標籤，使用者改動或清空後拿掉。</summary>
    [ObservableProperty]
    private bool _isVoiceDraft;

    private string? _voiceDraftText;

    /// <summary>要求把焦點移到輸入框（語音轉寫放進輸入框之後）。由 ChatComposer 處理。</summary>
    public event EventHandler? FocusInputRequested;

    [RelayCommand(CanExecute = nameof(CanStartVoice))]
    private Task StartVoiceAsync() => Voice.StartAsync(VoiceOrigin.Workbench);

    private bool CanStartVoice() => Voice.CanStart;

    /// <summary>「停止並編輯」。</summary>
    [RelayCommand]
    private Task VoiceStopEditAsync() => Voice.StopAsync(VoiceStopAction.Edit);

    /// <summary>「停止並送出」。</summary>
    [RelayCommand]
    private Task VoiceStopSendAsync() => Voice.StopAsync(VoiceStopAction.Send);

    [RelayCommand]
    private void VoiceCancel() => Voice.Cancel();

    [RelayCommand]
    private Task VoiceRetryAsync() => Voice.RetryAsync();

    [RelayCommand]
    private void VoiceDiscard() => Voice.Discard();

    private void OnVoicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 提示只顯示在開始錄音的那一邊（小精靈的提示顯示在氣泡）
        if (e.PropertyName is null or "" or nameof(IVoiceInputController.Notice))
            VoiceNotice = Voice.Origin == VoiceOrigin.Sprite ? null : Voice.Notice;
        RaiseVoiceProperties();
    }

    /// <summary>控制器或回應狀態變了：通知輸入區相關的屬性與命令。加入檔案／資料夾（Context）在回應中與語音進行中都鎖住。</summary>
    private void RaiseVoiceProperties()
    {
        Context.IsLocked = IsResponding || IsVoiceActive;
        OnPropertyChanged(nameof(VoiceState));
        OnPropertyChanged(nameof(IsVoiceActive));
        OnPropertyChanged(nameof(IsVoiceRecording));
        OnPropertyChanged(nameof(IsVoiceTranscribing));
        OnPropertyChanged(nameof(IsVoiceFailed));
        OnPropertyChanged(nameof(VoiceCanStart));
        OnPropertyChanged(nameof(MicTip));
        OnPropertyChanged(nameof(ShowSendButton));
        OnPropertyChanged(nameof(ShowSendHint));
        OnPropertyChanged(nameof(VoiceTimeText));
        OnPropertyChanged(nameof(VoiceStatusText));
        OnPropertyChanged(nameof(VoiceLevel));
        OnPropertyChanged(nameof(VoiceFailureText));
        OnPropertyChanged(nameof(AddFileTip));
        OnPropertyChanged(nameof(AddFolderTip));
        StartVoiceCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 轉寫完成。送出：同一則對話、沒有在回應、輸入框是空的，就走現有的送出路徑（<see cref="SendCommand"/>）；
    /// 其他情況（對話已改變、回應中、輸入框已有草稿）一律放進輸入框並提示，不自動送出（計畫 6D）。
    /// </summary>
    private void OnVoiceTranscribed(object? sender, VoiceTranscribedArgs args)
    {
        if (args.Origin != VoiceOrigin.Workbench)
            return;   // 小精靈開始的錄音由小精靈處理

        var sameConversation = args.ConversationId is not null && args.ConversationId == CurrentConversationId;
        if (args.Action == VoiceStopAction.Send && sameConversation && !IsResponding && string.IsNullOrWhiteSpace(InputText))
        {
            InputText = args.Text;
            SendCommand.Execute(null);
            return;
        }

        string? notice = null;
        if (!sameConversation || IsResponding)
            notice = Strings.Voice_NoticeEditOnly;
        else if (args.Action == VoiceStopAction.Send)
            notice = Strings.Voice_NoticeHasDraft;
        AppendVoiceDraft(args.Text, notice);
    }

    /// <summary>把語音文字接在輸入框的草稿後面（以換行分隔），標上「語音轉寫」，並把焦點移到輸入框。</summary>
    private void AppendVoiceDraft(string text, string? notice)
    {
        var draft = InputText;
        InputText = draft.Trim().Length == 0 ? text : draft.TrimEnd('\r', '\n') + "\n" + text;
        _voiceDraftText = InputText;
        IsVoiceDraft = true;
        VoiceNotice = notice;   // 最後設：上面設定文字時會清掉舊的提示
        FocusInputRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatClock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    // ---- 複製 ----

    [RelayCommand]
    private async Task CopyAsync(MessageViewModel? message)
    {
        if (message is null || !message.HasText)
            return;

        await _clipboard.SetTextAsync(message.Text);
        message.IsCopied = true;
        _ = ResetCopiedAsync(message);
    }

    private async Task ResetCopiedAsync(MessageViewModel message)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), _time);
        }
        finally
        {
            message.IsCopied = false;
        }
    }

    // ---- 回應流程：會話發事件，這裡更新畫面 ----

    /// <summary>工作台自己發起的一輪：會話跑完（含畫面收尾）才回來，接著處理失敗。</summary>
    private async Task RunResponseAsync(Func<Task<ChatRunResult>> run, bool reloadOnFailure)
    {
        var conversationId = CurrentConversationId!;
        ChatRunResult outcome;
        _ownRun = true;
        try
        {
            outcome = await run();
        }
        finally
        {
            _ownRun = false;
        }

        // 這之後 IsResponding 已經是 false、StopAndWaitAsync 也不會再等：金鑰視窗可能開很久，
        // 期間使用者可以自由切換對話，所以下面每一步都要確認「還是當初那則對話」。
        if (outcome.Unexpected)
        {
            if (conversationId == CurrentConversationId)
                ErrorMessage = Strings.Workbench_UnexpectedError;
            await History.RefreshAsync(CurrentConversationId);
            return;
        }

        if (outcome.Failure is not { } failure)
        {
            await History.RefreshAsync(CurrentConversationId);
            return;
        }

        if (reloadOnFailure && conversationId == CurrentConversationId)
            await ReloadMessagesAsync();   // 重新產生失敗時，資料庫裡舊答案可能已經被移除了：畫面跟著重讀

        if (ToPromptReason(failure.Kind) is { } reason && await _apiKeyPrompt.PromptAsync(reason))
        {
            if (conversationId == CurrentConversationId && !IsResponding)
                await RetryAsync();
            return;
        }

        if (conversationId == CurrentConversationId)
            ErrorMessage = failure.Message;
        await History.RefreshAsync(CurrentConversationId);
    }

    private void OnRunStarted(object? sender, RunStartedArgs args)
    {
        _inCompaction = false;
        ErrorMessage = null;
        IsResponding = true;

        if (args.Kind == ChatRunKind.Regenerate)
        {
            // 重新產生會移除整輪（氣泡與工具段落都要一起移除，一路到最後一則使用者訊息）
            while (Messages.LastOrDefault() is { IsUser: false } old)
                Messages.Remove(old);
        }
        else if (!_ownRun && args.Kind == ChatRunKind.Send && args.UserText is { } text)
        {
            // 別處（小精靈）送出的：同一則對話，使用者的訊息也要出現在這裡
            if (Messages.Count == 0)
                Title = ConversationService.MakeTitle(text);
            Messages.Add(MessageViewModel.User(text, createdAt: _time.GetUtcNow()));
        }

        _cards.Clear();
        _usedTools = false;
        _runConnectionId = WorkbenchModel?.Connection.Id;

        // 等待回應的氣泡（還沒有文字時顯示「…」）。一輪裡每次呼叫供應商各用一個氣泡；
        // 呼叫工具之間沒有文字的話不留氣泡。
        _bubble = StartBubble();
    }

    private MessageViewModel StartBubble()
    {
        var created = MessageViewModel.StreamingAssistant(_time.GetUtcNow());
        Messages.Add(created);
        RefreshMessageState();
        return created;
    }

    private void DropBubble()
    {
        if (_bubble is not null)
            Messages.Remove(_bubble);
        _bubble = null;
    }

    private void OnEventReceived(object? sender, ChatEvent chatEvent)
    {
        switch (chatEvent)
        {
            case TextDelta delta:
                (_bubble ??= StartBubble()).AppendText(delta.Text, _time.GetUtcNow());
                break;
            case ThinkingDelta delta:
                (_bubble ??= StartBubble()).AppendThinking(delta.Text, _time.GetUtcNow());
                break;
            case UsageEvent usage:
                _tally.ReportInFlight(usage.Tokens);   // 進行中請求的累計值（覆蓋，不相加）
                if (!_inCompaction)
                    LastUsage = usage.Tokens;   // 摘要呼叫的用量不是目前的上下文大小
                RefreshUsage();
                break;
            case CompactionStarted or CompactionCompleted or CompactionFailed:
                OnCompactionEvent(chatEvent);
                OnCompactionDisplayEvent(chatEvent);
                break;
            case Completed completed when completed.Reason == StopReason.ToolUse:
                // 這一輪還沒結束：有文字就定稿成一個氣泡，只有思考或只有工具呼叫的中間步驟不顯示（Q5）
                LastUsage = completed.Message.Usage ?? LastUsage;
                CommitUsage(completed);
                if (completed.Message.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0))
                    (_bubble ??= StartBubble()).Complete(completed.Message, _time.GetUtcNow());
                else
                    DropBubble();
                _bubble = null;
                break;
            case Completed completed:
                (_bubble ??= StartBubble()).Complete(completed.Message, _time.GetUtcNow());
                _bubble = null;
                LastUsage = completed.Message.Usage ?? LastUsage;
                CommitUsage(completed);
                if (_runConnectionId is not null)
                    _status.Mark(_runConnectionId, ConnectionStatus.Ok);
                break;
            case ToolCallStarted started:
            {
                _usedTools = true;
                if (_bubble is { HasText: false, HasThinking: false })
                    DropBubble();   // 等下一次回應用的空氣泡讓位給新的卡片

                if (Messages.LastOrDefault() is not ToolRunViewModel run)
                {
                    run = new ToolRunViewModel();
                    Messages.Add(run);
                }
                var card = NewCard(started.CallId, started.ToolName, started.DisplayName, started.Permission, started.Target, started.ArgumentsJson);
                _cards[started.CallId] = card;
                run.Cards.Add(card);
                RefreshMessageState();
                break;
            }
            case ApprovalRequired approval:
                if (_cards.TryGetValue(approval.Request.CallId, out var waiting))
                    waiting.SetAwaiting(approval.Request);
                break;
            case ToolCallProgress progress:
                if (_cards.TryGetValue(progress.CallId, out var progressCard))
                    progressCard.SetProgress(progress.Text);
                break;
            case ToolCallCompleted finished:
                if (_cards.TryGetValue(finished.CallId, out var finishedCard))
                    finishedCard.Complete(finished.Result);
                _bubble ??= StartBubble();   // 工具跑完，等模型根據結果回應
                break;
            case Failed failed:
                if (_runConnectionId is not null && ConnectionStatusCache.StatusAfterFailure(failed.Failure.Kind) is { } status)
                    _status.Mark(_runConnectionId, status);
                break;
        }
    }

    private void OnRunFinished(object? sender, RunFinishedArgs args)
    {
        var result = args.Result;
        if (result.Cancelled)
        {
            // 使用者按了停止：有文字就標示已停止（ConversationService 已存檔），沒有就移除
            if (_bubble is { IsStreaming: true })
            {
                if (_bubble.HasText)
                    _bubble.MarkStopped(_time.GetUtcNow());
                else
                    DropBubble();
            }
        }

        // 失敗的回應不存檔（Q15），畫面上也移除，改顯示錯誤列。前面已經完成的氣泡與工具卡片都存了，保留。
        if ((result.Unexpected || result.Failure is not null) && _bubble is { IsStreaming: true })
            DropBubble();

        IsResponding = false;
        RefreshMessageState();
        OnCompactionRunFinished(args);

        // 用量：沒有定稿的那次請求不計；失敗或中止時以資料庫實際存下來的為準重算
        _tally.Abandon();
        RefreshUsage();
        if ((result.Cancelled || result.Unexpected || result.Failure is not null) && CurrentConversationId is { } usageConversation)
            args.Defer(ReloadUsageAsync(usageConversation));
        args.Defer(RefreshCostAsync());   // 費用：這一輪的紀錄已經寫入（失敗或中止時存下來的部分照算）

        // 有用到工具的這一輪被停止：ConversationService 已經補好「已停止」結果並存檔，畫面從資料庫重讀才和存檔一致
        if (result.Cancelled && _usedTools && CurrentConversationId is not null)
            args.Defer(ReloadMessagesAsync());

        if (!_ownRun)
        {
            // 小精靈發起的：失敗只顯示在錯誤列（金鑰提示與重送由發起的那一邊處理）
            if (result.Unexpected)
                ErrorMessage = Strings.Workbench_UnexpectedError;
            else if (result.Failure is { } failure)
                ErrorMessage = failure.Message;
            args.Defer(History.RefreshAsync(CurrentConversationId));
        }

        _bubble = null;
        _cards.Clear();
    }

    /// <summary>別處（小精靈的氣泡）回答了權限確認：這裡的卡片同步更新（ui-spec 2.2）。</summary>
    private void OnApprovalResolved(object? sender, ApprovalResolved resolved)
    {
        if (_cards.TryGetValue(resolved.CallId, out var card))
            card.ApplyResolved(resolved.Decision);
    }

    private static ApiKeyPromptReason? ToPromptReason(ChatFailureKind kind) => kind switch
    {
        ChatFailureKind.MissingApiKey => ApiKeyPromptReason.Missing,
        ChatFailureKind.InvalidApiKey => ApiKeyPromptReason.Invalid,
        ChatFailureKind.SecretUnreadable => ApiKeyPromptReason.Unreadable,
        _ => null,
    };

    /// <summary>950、14.2K、1M 這種簡短格式。</summary>
    public static string FormatTokens(int tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => FormatScaled(tokens / 1_000.0, "K"),
        _ => FormatScaled(tokens / 1_000_000.0, "M"),
    };

    private static string FormatScaled(double value, string suffix) =>
        (value < 10 ? value.ToString("0.#", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture)) + suffix;
}
