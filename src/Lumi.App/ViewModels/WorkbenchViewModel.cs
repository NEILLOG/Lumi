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

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台主視窗。命令都在 UI 執行緒上執行；<c>await foreach</c> 會回到 UI 的同步內容，
/// 所以串流事件直接在 UI 執行緒更新畫面，不需要另外轉送（agent.md 第 12 節）。
/// </summary>
public partial class WorkbenchViewModel : ObservableObject
{
    private readonly ConversationService _conversations;
    private readonly IConversationStore _store;
    private readonly IModelCatalog _catalog;
    private readonly IApiKeyPrompt _apiKeyPrompt;
    private readonly ISettingsWindowService _settingsWindow;
    private readonly ConnectionStatusCache _status;
    private readonly IClipboardService _clipboard;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkbenchViewModel> _logger;
    private readonly IToolApprovalResponder _approvals;

    private string? _conversationId;
    private CancellationTokenSource? _responseCts;
    private TaskCompletionSource? _responseDone;

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
        IToolApprovalResponder? approvals = null)
    {
        _conversations = conversations;
        _store = store;
        _catalog = catalog;
        _apiKeyPrompt = apiKeyPrompt;
        _settingsWindow = settingsWindow;
        _status = status;
        _clipboard = clipboard;
        _time = time;
        _logger = logger;
        _approvals = approvals ?? NullToolApprovalResponder.Instance;
        _notice = catalog.LoadProblem;

        History = new HistoryViewModel(store, OpenConversationAsync, time);
        ModelSwitcher = new ModelSwitcherViewModel(catalog, status, settingsWindow, dispatcher, () => IsResponding);
        // 錯誤列的「換模型重試」：選完模型接著重送
        RetrySwitcher = new ModelSwitcherViewModel(catalog, status, settingsWindow, dispatcher, () => IsResponding, RetryAfterModelSwitchAsync);

        catalog.Changed += (_, _) => dispatcher.Post(OnModelSettingsChanged);
        status.Changed += (_, _) => dispatcher.Post(OnConnectionStatusChanged);
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public HistoryViewModel History { get; }

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
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ContextWindow));
        OnPropertyChanged(nameof(ContextText));
        OnConnectionStatusChanged();
    }

    // ---- 連線狀態（狀態列右側） ----

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
    [NotifyPropertyChangedFor(nameof(ShowSwitchBlockedHint), nameof(CanSwitchModel))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(RetryCommand), nameof(RegenerateCommand))]
    private bool _isResponding;

    /// <summary>回應中，膠囊與小精靈按鈕停用並顯示「回應中，暫時無法切換」（ui-spec 2.2）。</summary>
    public bool ShowSwitchBlockedHint => IsResponding;

    public bool CanSwitchModel => !IsResponding;

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
        var conversation = await _store.GetLatestAsync()
                           ?? await _store.CreateAsync(Strings.Workbench_NewConversationTitle);
        await LoadAsync(conversation);
    }

    // ---- 送出、重試、重新產生、停止 ----

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        var conversationId = _conversationId!;
        InputText = "";
        if (Messages.Count == 0)
            Title = ConversationService.MakeTitle(text);   // 與 ConversationService 存進資料庫的標題相同
        Messages.Add(MessageViewModel.User(text));
        await RunResponseAsync(ct => _conversations.SendAsync(conversationId, text, ct), reloadOnFailure: false);
    }

    private bool CanSend() => !IsResponding && _conversationId is not null && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync()
    {
        var conversationId = _conversationId!;
        return RunResponseAsync(ct => _conversations.RetryAsync(conversationId, ct), reloadOnFailure: false);
    }

    private bool CanRetry() => !IsResponding && HasError;

    /// <summary>「換模型重試」：模型已經切好了，接著重送（只有最後一則是使用者訊息時才有東西可重送）。</summary>
    private Task RetryAfterModelSwitchAsync() => RetryCommand.CanExecute(null) ? RetryAsync() : Task.CompletedTask;

    /// <summary>重新產生：移除最後一則助理訊息，用同一段歷史再要一次；新回應取代舊的。</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private Task RegenerateAsync()
    {
        var conversationId = _conversationId!;
        ErrorMessage = null;
        return RunResponseAsync(ct => _conversations.RegenerateAsync(conversationId, ct), reloadOnFailure: true, removeLastAssistantFirst: true);
    }

    private bool CanRegenerate() => !IsResponding && _conversationId is not null && Messages.LastOrDefault() is { IsUser: false };

    [RelayCommand(CanExecute = nameof(IsResponding))]
    private void Stop() => _responseCts?.Cancel();

    /// <summary>取消目前的回應，並等它的收尾全部做完（存「已停止」、移除空泡泡）才回來。沒有回應進行中就直接回來。</summary>
    public async Task StopAndWaitAsync()
    {
        if (_responseDone is not { } done)
            return;
        _responseCts?.Cancel();
        await done.Task;
    }

    // ---- 對話：新增、切換 ----

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        // 目前這則還沒有任何訊息：已經是空白的了，不重複建立
        if (Messages.Count == 0 && _conversationId is not null)
            return;

        await StopAndWaitAsync();
        var conversation = await _store.CreateAsync(Strings.Workbench_NewConversationTitle);
        await LoadAsync(conversation);
        await History.RefreshAsync(_conversationId);
    }

    /// <summary>從歷史抽屜切換對話。回應中會先停止（ui-spec 2.2）。</summary>
    public async Task OpenConversationAsync(Conversation conversation)
    {
        if (conversation.Id == _conversationId)
            return;

        await StopAndWaitAsync();
        await LoadAsync(conversation);
        await History.RefreshAsync(_conversationId);
    }

    private async Task LoadAsync(Conversation conversation)
    {
        _conversationId = conversation.Id;
        Title = conversation.Title;
        ErrorMessage = null;
        await ReloadMessagesAsync();
    }

    /// <summary>從資料庫重讀目前對話的訊息（讓畫面和資料庫一致）。</summary>
    private async Task ReloadMessagesAsync()
    {
        var messages = await _store.GetMessagesAsync(_conversationId!);
        Messages.Clear();
        foreach (var item in ConversationTimeline.Build(messages, use => NewCard(use.Id, use.Name, use.Name, null, null, use.InputJson)))
            Messages.Add(item);

        LastUsage = messages.LastOrDefault(m => m.Usage is not null)?.Usage;
        RefreshMessageState();
        SendCommand.NotifyCanExecuteChanged();
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

    // ---- 回應流程 ----

    private sealed record StreamOutcome(ChatFailure? Failure, bool Unexpected);

    private async Task RunResponseAsync(
        Func<CancellationToken, IAsyncEnumerable<ChatEvent>> start, bool reloadOnFailure, bool removeLastAssistantFirst = false)
    {
        var conversationId = _conversationId!;
        var outcome = await StreamPhaseAsync(start, removeLastAssistantFirst);

        // 這之後 IsResponding 已經是 false、StopAndWaitAsync 也不會再等：金鑰視窗可能開很久，
        // 期間使用者可以自由切換對話，所以下面每一步都要確認「還是當初那則對話」。
        if (outcome.Unexpected)
        {
            if (conversationId == _conversationId)
                ErrorMessage = Strings.Workbench_UnexpectedError;
            await History.RefreshAsync(_conversationId);
            return;
        }

        if (outcome.Failure is not { } failure)
        {
            await History.RefreshAsync(_conversationId);
            return;
        }

        if (reloadOnFailure && conversationId == _conversationId)
            await ReloadMessagesAsync();   // 重新產生失敗時，資料庫裡舊答案可能已經被移除了：畫面跟著重讀

        if (ToPromptReason(failure.Kind) is { } reason && await _apiKeyPrompt.PromptAsync(reason))
        {
            if (conversationId == _conversationId && !IsResponding)
                await RetryAsync();
            return;
        }

        if (conversationId == _conversationId)
            ErrorMessage = failure.Message;
        await History.RefreshAsync(_conversationId);
    }

    private async Task<StreamOutcome> StreamPhaseAsync(Func<CancellationToken, IAsyncEnumerable<ChatEvent>> start, bool removeLastAssistantFirst)
    {
        var done = new TaskCompletionSource();
        _responseDone = done;
        ErrorMessage = null;
        IsResponding = true;

        // 重新產生會移除整輪（氣泡與工具段落都要一起移除，一路到最後一則使用者訊息）
        while (removeLastAssistantFirst && Messages.LastOrDefault() is { IsUser: false } old)
            Messages.Remove(old);

        // 等待回應的氣泡（還沒有文字時顯示「…」）。一輪裡每次呼叫供應商各用一個氣泡；
        // 呼叫工具之間沒有文字的話不留氣泡。
        var bubble = StartBubble();
        var cards = new Dictionary<string, ToolCardViewModel>(StringComparer.Ordinal);
        var usedTools = false;

        MessageViewModel StartBubble()
        {
            var created = MessageViewModel.StreamingAssistant();
            Messages.Add(created);
            RefreshMessageState();
            return created;
        }

        void DropBubble()
        {
            if (bubble is not null)
                Messages.Remove(bubble);
            bubble = null;
        }

        var connectionId = WorkbenchModel?.Connection.Id;
        using var cts = new CancellationTokenSource();
        _responseCts = cts;
        ChatFailure? failure = null;
        var unexpected = false;
        var cancelled = false;
        try
        {
            try
            {
                await foreach (var chatEvent in start(cts.Token))
                {
                    switch (chatEvent)
                    {
                        case TextDelta delta:
                            (bubble ??= StartBubble()).AppendText(delta.Text, _time.GetUtcNow());
                            break;
                        case ThinkingDelta delta:
                            (bubble ??= StartBubble()).AppendThinking(delta.Text, _time.GetUtcNow());
                            break;
                        case UsageEvent usage:
                            LastUsage = usage.Tokens;
                            break;
                        case Completed completed when completed.Reason == StopReason.ToolUse:
                            // 這一輪還沒結束：有文字就定稿成一個氣泡，只有思考或只有工具呼叫的中間步驟不顯示（Q5）
                            LastUsage = completed.Message.Usage ?? LastUsage;
                            if (completed.Message.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0))
                                (bubble ??= StartBubble()).Complete(completed.Message, _time.GetUtcNow());
                            else
                                DropBubble();
                            bubble = null;
                            break;
                        case Completed completed:
                            (bubble ??= StartBubble()).Complete(completed.Message, _time.GetUtcNow());
                            bubble = null;
                            LastUsage = completed.Message.Usage ?? LastUsage;
                            if (connectionId is not null)
                                _status.Mark(connectionId, ConnectionStatus.Ok);
                            break;
                        case ToolCallStarted started:
                        {
                            usedTools = true;
                            if (bubble is { HasText: false, HasThinking: false })
                                DropBubble();   // 等下一次回應用的空氣泡讓位給新的卡片

                            if (Messages.LastOrDefault() is not ToolRunViewModel run)
                            {
                                run = new ToolRunViewModel();
                                Messages.Add(run);
                            }
                            var card = NewCard(started.CallId, started.ToolName, started.DisplayName, started.Permission, started.Target, started.ArgumentsJson);
                            cards[started.CallId] = card;
                            run.Cards.Add(card);
                            RefreshMessageState();
                            break;
                        }
                        case ApprovalRequired approval:
                            if (cards.TryGetValue(approval.Request.CallId, out var waiting))
                                waiting.SetAwaiting(approval.Request);
                            break;
                        case ToolCallCompleted finished:
                            if (cards.TryGetValue(finished.CallId, out var finishedCard))
                                finishedCard.Complete(finished.Result);
                            bubble ??= StartBubble();   // 工具跑完，等模型根據結果回應
                            break;
                        case Failed failed:
                            failure = failed.Failure;
                            if (connectionId is not null && ConnectionStatusCache.StatusAfterFailure(failed.Failure.Kind) is { } status)
                                _status.Mark(connectionId, status);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                cancelled = true;
                // 使用者按了停止：有文字就標示已停止（ConversationService 已存檔），沒有就移除
                if (bubble is { IsStreaming: true })
                {
                    if (bubble.HasText)
                        bubble.MarkStopped(_time.GetUtcNow());
                    else
                        DropBubble();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "對話回應發生未預期的錯誤");
                unexpected = true;
            }

            // 失敗的回應不存檔（Q15），畫面上也移除，改顯示錯誤列。前面已經完成的氣泡與工具卡片都存了，保留。
            if ((unexpected || failure is not null) && bubble is { IsStreaming: true })
                DropBubble();
        }
        finally
        {
            _responseCts = null;
            _responseDone = null;
            IsResponding = false;
            RefreshMessageState();
            try
            {
                // 有用到工具的這一輪被停止：ConversationService 已經補好「已停止」結果並存檔，畫面從資料庫重讀才和存檔一致
                if (cancelled && usedTools && _conversationId is not null)
                    await ReloadMessagesAsync();
            }
            finally
            {
                done.TrySetResult();   // 收尾全部做完才通知：StopAndWaitAsync 之後可以放心換掉訊息列表
            }
        }

        return new StreamOutcome(failure, unexpected);
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
