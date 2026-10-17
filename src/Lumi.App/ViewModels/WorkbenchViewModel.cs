using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Chat;
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
    private readonly ChatDefaults _defaults;
    private readonly IApiKeyPrompt _apiKeyPrompt;
    private readonly ILogger<WorkbenchViewModel> _logger;

    private string? _conversationId;
    private CancellationTokenSource? _responseCts;

    public WorkbenchViewModel(
        ConversationService conversations,
        IConversationStore store,
        ChatDefaults defaults,
        IApiKeyPrompt apiKeyPrompt,
        ILogger<WorkbenchViewModel> logger)
    {
        _conversations = conversations;
        _store = store;
        _defaults = defaults;
        _apiKeyPrompt = apiKeyPrompt;
        _logger = logger;
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public string ModelName => _defaults.ModelId;

    public int ContextWindow => _defaults.ContextWindow;

    [ObservableProperty]
    private string _title = Strings.Workbench_Title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(RetryCommand), nameof(NewConversationCommand))]
    private bool _isResponding;

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

    public string ContextText => $"{FormatTokens(ContextUsed)} / {FormatTokens(ContextWindow)}";

    /// <summary>啟動時載入最近一則對話；沒有就建一則空的。</summary>
    public async Task InitializeAsync()
    {
        var conversation = await _store.GetLatestAsync()
                           ?? await _store.CreateAsync(Strings.Workbench_NewConversationTitle);
        await LoadAsync(conversation);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        var conversationId = _conversationId!;
        InputText = "";
        if (Messages.Count == 0)
            Title = ConversationService.MakeTitle(text);   // 與 ConversationService 存進資料庫的標題相同
        Messages.Add(MessageViewModel.User(text));
        await RunResponseAsync(ct => _conversations.SendAsync(conversationId, text, ct));
    }

    private bool CanSend() => !IsResponding && _conversationId is not null && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync()
    {
        var conversationId = _conversationId!;
        return RunResponseAsync(ct => _conversations.RetryAsync(conversationId, ct));
    }

    private bool CanRetry() => !IsResponding && HasError;

    [RelayCommand(CanExecute = nameof(IsResponding))]
    private void Stop() => _responseCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanStartNewConversation))]
    private async Task NewConversationAsync()
    {
        var conversation = await _store.CreateAsync(Strings.Workbench_NewConversationTitle);
        await LoadAsync(conversation);
    }

    private bool CanStartNewConversation() => !IsResponding;

    private async Task LoadAsync(Conversation conversation)
    {
        _conversationId = conversation.Id;
        Title = conversation.Title;
        ErrorMessage = null;
        Messages.Clear();

        var messages = await _store.GetMessagesAsync(conversation.Id);
        foreach (var message in messages)
            Messages.Add(MessageViewModel.From(message));

        Turns = messages.Count(m => m.Role == ChatRole.Assistant);
        LastUsage = messages.LastOrDefault(m => m.Usage is not null)?.Usage;
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task RunResponseAsync(Func<CancellationToken, IAsyncEnumerable<ChatEvent>> start)
    {
        ErrorMessage = null;
        IsResponding = true;
        var assistant = MessageViewModel.StreamingAssistant();
        Messages.Add(assistant);

        using var cts = new CancellationTokenSource();
        _responseCts = cts;
        ChatFailure? failure = null;
        var unexpected = false;
        try
        {
            await foreach (var chatEvent in start(cts.Token))
            {
                switch (chatEvent)
                {
                    case TextDelta delta:
                        assistant.AppendText(delta.Text);
                        break;
                    case ThinkingDelta delta:
                        assistant.AppendThinking(delta.Text);
                        break;
                    case UsageEvent usage:
                        LastUsage = usage.Tokens;
                        break;
                    case Completed completed:
                        assistant.Complete(completed.Message);
                        LastUsage = completed.Message.Usage ?? LastUsage;
                        Turns++;
                        break;
                    case Failed failed:
                        failure = failed.Failure;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 使用者按了停止：有文字就標示已停止（ConversationService 已存檔），沒有就移除
            if (assistant.HasText)
            {
                assistant.MarkStopped();
                Turns++;
            }
            else
            {
                Messages.Remove(assistant);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "對話回應發生未預期的錯誤");
            unexpected = true;
        }
        finally
        {
            _responseCts = null;
            IsResponding = false;
        }

        if (unexpected)
        {
            Messages.Remove(assistant);
            ErrorMessage = Strings.Workbench_UnexpectedError;
            return;
        }

        if (failure is null)
            return;

        // 失敗的回應不存檔（Q15），畫面上也移除，改顯示錯誤列
        Messages.Remove(assistant);
        if (ToPromptReason(failure.Kind) is { } reason && await _apiKeyPrompt.PromptAsync(reason))
        {
            await RetryAsync();
            return;
        }
        ErrorMessage = failure.Message;
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
