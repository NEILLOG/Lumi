using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台主視窗。對話與回應流程在 <see cref="IChatSession"/>（和小精靈共用），這裡是它的觀察者：
/// 收到事件就更新訊息列表。命令都在 UI 執行緒上執行，會話的事件也在呼叫者的同步內容上發出，
/// 所以直接更新畫面，不需要另外轉送（agent.md 第 12 節）。
/// </summary>
public partial class WorkbenchViewModel : ObservableObject
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
        IAppModeCoordinator? modes = null)
    {
        _store = store;
        _catalog = catalog;
        _apiKeyPrompt = apiKeyPrompt;
        _settingsWindow = settingsWindow;
        _status = status;
        _clipboard = clipboard;
        _time = time;
        _logger = logger;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowWelcome));
        _approvals = approvals ?? NullToolApprovalResponder.Instance;
        _session = session ?? new ChatSession(conversations, store, NullLogger<ChatSession>.Instance);
        _modes = modes;
        Context = contextBar ?? ContextBarViewModel.CreateInert(store, time);
        _notice = catalog.LoadProblem;

        History = new HistoryViewModel(store, OpenConversationAsync, time);
        ModelSwitcher = new ModelSwitcherViewModel(catalog, status, settingsWindow, dispatcher, () => IsResponding);
        ModelSwitcher.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelSwitcherViewModel.CapsuleTooltip))
                OnPropertyChanged(nameof(ModelCapsuleTip));
        };
        // 錯誤列的「換模型重試」：選完模型接著重送
        RetrySwitcher = new ModelSwitcherViewModel(catalog, status, settingsWindow, dispatcher, () => IsResponding, RetryAfterModelSwitchAsync);
        SyncSessionUsage();

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

        catalog.Changed += (_, _) => dispatcher.Post(OnModelSettingsChanged);
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
    private void OpenSettings() => _ = _settingsWindow.ShowAsync(new SettingsNavigation(SettingsSection.Connections));

    private void OnModelSettingsChanged()
    {
        Context.ContextWindow = ContextWindow;
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ContextWindow));
        OnPropertyChanged(nameof(ContextText));
        SyncSessionUsage();
        OnConnectionStatusChanged();
    }

    /// <summary>把本對話的用量（輸入／輸出／回合、上下文）推給模型選單頂部的「本對話」區塊（底部狀態列已移除）。</summary>
    private void SyncSessionUsage() => ModelSwitcher.UpdateSession(StatusText, ContextUsed, ContextWindow, ContextText);

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
    }

    [ObservableProperty]
    private string _title = Strings.Workbench_Title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSwitchBlockedHint), nameof(CanSwitchModel), nameof(IsViewOnly), nameof(SpriteModeTip), nameof(ModelCapsuleTip), nameof(AddFileTip), nameof(AddFolderTip))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(RetryCommand), nameof(RegenerateCommand), nameof(EnterSpriteModeCommand))]
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

    partial void OnIsRespondingChanged(bool value) => Context.IsLocked = value;

    partial void OnInputTextChanged(string value) => Context.UpdateDraft(value);

    /// <summary>回應中，膠囊與小精靈按鈕停用並顯示「回應中，暫時無法切換」（ui-spec 2.2）。</summary>
    public bool ShowSwitchBlockedHint => IsResponding;

    public bool CanSwitchModel => !IsResponding;

    /// <summary>模型膠囊的 Tooltip：平時是供應商、model ID、連線狀態與費用說明；回應中膠囊停用，改說明為什麼（取代原本佔版面的提示文字）。</summary>
    public string ModelCapsuleTip => IsResponding ? Strings.Workbench_SwitchBlocked : ModelSwitcher.CapsuleTooltip;

    /// <summary>「小精靈模式」按鈕的 Tooltip：回應中說明為什麼停用，平時是按鈕名稱（窄版只剩圖示時靠它說明）。</summary>
    public string SpriteModeTip => IsResponding ? Strings.Workbench_SwitchBlocked : Strings.Workbench_SpriteMode;

    /// <summary>「加入檔案」「加入資料夾」的 Tooltip：回應中停用，改說明為什麼（取代原本佔版面的提示文字）。</summary>
    public string AddFileTip => IsResponding ? Strings.Composer_AddBlocked : Strings.Context_AddFile;

    public string AddFolderTip => IsResponding ? Strings.Composer_AddBlocked : Strings.Context_AddFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ContextUsed), nameof(ContextText))]
    private TokenUsage? _lastUsage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _turns;

    partial void OnLastUsageChanged(TokenUsage? value) => SyncSessionUsage();

    partial void OnTurnsChanged(int value) => SyncSessionUsage();

    public bool HasError => ErrorMessage is not null;

    private int InputTokensTotal => LastUsage is { } u ? u.InputTokens + u.CacheCreationInputTokens + u.CacheReadInputTokens : 0;

    public string StatusText => string.Format(
        CultureInfo.CurrentCulture, Strings.Workbench_StatusFormat,
        FormatTokens(InputTokensTotal), FormatTokens(LastUsage?.OutputTokens ?? 0), Turns);

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

    private bool CanSend() => !IsResponding && CurrentConversationId is not null && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync()
    {
        _modes?.WorkbenchWillSend();
        return RunResponseAsync(() => _session.RetryAsync(), reloadOnFailure: false);
    }

    private bool CanRetry() => !IsResponding && HasError;

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

    private bool CanRegenerate() => !IsResponding && CurrentConversationId is not null && Messages.LastOrDefault() is { IsUser: false };

    [RelayCommand(CanExecute = nameof(IsResponding))]
    private void Stop() => _session.Stop();

    /// <summary>取消目前的回應，並等它的收尾全部做完（存「已停止」、移除空泡泡）才回來。沒有回應進行中就直接回來。</summary>
    public Task StopAndWaitAsync() => _session.StopAndWaitAsync();

    // ---- 對話：新增、切換 ----

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        // 目前這則還沒有任何訊息：已經是空白的了，不重複建立
        if (Messages.Count == 0 && CurrentConversationId is not null)
            return;

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
    }

    /// <summary>從資料庫重讀目前對話的訊息（讓畫面和資料庫一致）。</summary>
    private async Task ReloadMessagesAsync()
    {
        var messages = await _store.GetMessagesAsync(CurrentConversationId!);
        Messages.Clear();
        foreach (var item in ConversationTimeline.Build(messages, use => NewCard(use.Id, use.Name, use.Name, null, null, use.InputJson)))
            Messages.Add(item);

        LastUsage = messages.LastOrDefault(m => m.Usage is not null)?.Usage;
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
                LastUsage = usage.Tokens;
                break;
            case Completed completed when completed.Reason == StopReason.ToolUse:
                // 這一輪還沒結束：有文字就定稿成一個氣泡，只有思考或只有工具呼叫的中間步驟不顯示（Q5）
                LastUsage = completed.Message.Usage ?? LastUsage;
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
