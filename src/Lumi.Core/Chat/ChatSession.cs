using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Chat;

/// <inheritdoc cref="IChatSession"/>
public sealed class ChatSession : IChatSession
{
    /// <summary>程式檔案不完整時給使用者看的說明；Core 不能用 App 的資源檔，和 <c>Strings.Chat_ErrorAppFilesMissing</c> 同字（App.Tests 守住一致）。</summary>
    public const string AppFilesMissingMessage = "Lumi 的程式檔案不完整，請重新啟動 Lumi；仍然發生請重新下載安裝。";

    private readonly ConversationService _conversations;
    private readonly IConversationStore _store;
    private readonly ILogger<ChatSession> _logger;

    private CancellationTokenSource? _responseCts;
    private TaskCompletionSource? _responseDone;

    public ChatSession(ConversationService conversations, IConversationStore store, ILogger<ChatSession>? logger = null)
    {
        _conversations = conversations;
        _store = store;
        _logger = logger ?? NullLogger<ChatSession>.Instance;
    }

    public string? ConversationId { get; private set; }

    public ChatMode ActiveMode { get; private set; } = ChatMode.Workbench;

    public bool IsResponding { get; private set; }

    public event EventHandler<ConversationChangedArgs>? ConversationChanged;

    public event EventHandler<RunStartedArgs>? RunStarted;

    public event EventHandler<ChatEvent>? EventReceived;

    public event EventHandler<RunFinishedArgs>? RunFinished;

    public event EventHandler<ChatMode>? ModeChanged;

    // ---- 對話：載入、切換、新增 ----

    public async Task<Conversation> InitializeAsync(string newConversationTitle, CancellationToken ct = default)
    {
        var conversation = await _store.GetLatestAsync(ct) ?? await _store.CreateAsync(newConversationTitle, ct);
        Select(conversation);
        return conversation;
    }

    public async Task<Conversation> OpenConversationAsync(Conversation conversation)
    {
        if (conversation.Id != ConversationId)
        {
            await StopAndWaitAsync();
            Select(conversation);
        }
        return conversation;
    }

    public async Task<Conversation> NewConversationAsync(string title)
    {
        await StopAndWaitAsync();
        var conversation = await _store.CreateAsync(title);
        Select(conversation);
        return conversation;
    }

    private void Select(Conversation conversation)
    {
        ConversationId = conversation.Id;
        Raise(ConversationChanged, new ConversationChangedArgs(conversation));
    }

    // ---- 模式 ----

    public bool TrySetMode(ChatMode mode)
    {
        if (IsResponding)
            return false;
        if (mode != ActiveMode)
        {
            ActiveMode = mode;
            Raise(ModeChanged, mode);
        }
        return true;
    }

    // ---- 回應 ----

    public Task<ChatRunResult> SendAsync(string text, CancellationToken ct = default) =>
        RunAsync(ChatRunKind.Send, text, ct, (id, mode, token) => _conversations.SendAsync(id, text, token, mode));

    public Task<ChatRunResult> RetryAsync(CancellationToken ct = default) =>
        RunAsync(ChatRunKind.Retry, null, ct, (id, mode, token) => _conversations.RetryAsync(id, token, mode));

    public Task<ChatRunResult> RegenerateAsync(CancellationToken ct = default) =>
        RunAsync(ChatRunKind.Regenerate, null, ct, (id, mode, token) => _conversations.RegenerateAsync(id, token, mode));

    public void Stop() => _responseCts?.Cancel();

    public async Task StopAndWaitAsync()
    {
        if (_responseDone is not { } done)
            return;
        _responseCts?.Cancel();
        await done.Task;
    }

    private async Task<ChatRunResult> RunAsync(
        ChatRunKind kind, string? userText, CancellationToken ct,
        Func<string, ChatMode, CancellationToken, IAsyncEnumerable<ChatEvent>> start)
    {
        var conversationId = ConversationId ?? throw new InvalidOperationException("還沒有載入任何對話。");
        if (IsResponding)
            throw new InvalidOperationException("上一輪回應還沒結束。");

        var mode = ActiveMode;   // 這一輪用送出當下的模式
        var done = new TaskCompletionSource();
        _responseDone = done;
        IsResponding = true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _responseCts = cts;
        ChatFailure? failure = null;
        var unexpected = false;
        var cancelled = false;
        ChatRunResult result;
        try
        {
            try
            {
                Raise(RunStarted, new RunStartedArgs(conversationId, mode, kind, userText));
                await foreach (var chatEvent in start(conversationId, mode, cts.Token))
                {
                    if (chatEvent is Failed failed)
                        failure = failed.Failure;
                    Raise(EventReceived, chatEvent);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception ex) when (MissingAppFiles.Is(ex))
            {
                // 程式檔案（組件）載入不到：不是使用者能處理的一般錯誤，給一句明確的說明（M22）
                _logger.LogError(ex, "對話回應時找不到程式檔案");
                failure = new ChatFailure(ChatFailureKind.AppFilesMissing, AppFilesMissingMessage, Retryable: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "對話回應發生未預期的錯誤");
                unexpected = true;
            }
        }
        finally
        {
            _responseCts = null;
            IsResponding = false;
            result = new ChatRunResult(conversationId, unexpected ? null : failure, unexpected, cancelled);
            try
            {
                var args = new RunFinishedArgs(result);
                Raise(RunFinished, args);
                await WaitForDeferredAsync(args);
            }
            finally
            {
                // 收尾全部做完才通知：StopAndWaitAsync 之後可以放心換掉訊息列表
                done.TrySetResult();
                if (ReferenceEquals(_responseDone, done))
                    _responseDone = null;
            }
        }
        return result;
    }

    private async Task WaitForDeferredAsync(RunFinishedArgs args)
    {
        try
        {
            await args.WhenAllDeferred();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "回應結束後的收尾失敗");
        }
    }

    /// <summary>逐一呼叫觀察者；一個丟例外不影響其他觀察者，也不中斷回應。</summary>
    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
            return;
        foreach (var single in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)single)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "對話事件的觀察者丟出例外");
            }
        }
    }
}
