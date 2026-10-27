using Lumi.Core.Chat;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 只給語音控制器用的假對話會話：可以改對話 ID、IsResponding，並手動發出 RunStarted、RunFinished、ConversationChanged。
/// 其他方法（送出、切換模式…）不在語音測試的範圍，呼叫就丟 <see cref="NotSupportedException"/>。
/// </summary>
internal sealed class FakeChatSession : IChatSession
{
    public string? ConversationId { get; set; } = "conv-1";

    public ChatMode ActiveMode { get; set; } = ChatMode.Workbench;

    public bool IsResponding { get; set; }

    public event EventHandler<ConversationChangedArgs>? ConversationChanged;
    public event EventHandler<RunStartedArgs>? RunStarted;
    public event EventHandler<ChatEvent>? EventReceived;
    public event EventHandler<RunFinishedArgs>? RunFinished;
    public event EventHandler<ChatMode>? ModeChanged;

    /// <summary>模擬一輪回應開始（同時把 IsResponding 設為 true）。</summary>
    public void StartRun()
    {
        IsResponding = true;
        RunStarted?.Invoke(this, new RunStartedArgs(ConversationId!, ActiveMode, ChatRunKind.Send, "x"));
    }

    /// <summary>發出一個回應事件（語音測試用不到，但介面要求的事件要有人能發）。</summary>
    public void Emit(ChatEvent chatEvent) => EventReceived?.Invoke(this, chatEvent);

    /// <summary>模擬切換模式。</summary>
    public void SetMode(ChatMode mode)
    {
        ActiveMode = mode;
        ModeChanged?.Invoke(this, mode);
    }

    /// <summary>模擬一輪回應結束。</summary>
    public void FinishRun()
    {
        IsResponding = false;
        RunFinished?.Invoke(this, new RunFinishedArgs(new ChatRunResult(ConversationId!, null, false, false)));
    }

    /// <summary>模擬切換到另一則對話。</summary>
    public void SwitchConversation(string conversationId)
    {
        ConversationId = conversationId;
        var now = DateTimeOffset.UnixEpoch;
        ConversationChanged?.Invoke(this, new ConversationChangedArgs(new Conversation(conversationId, "對話", now, now)));
    }

    public Task<Conversation> InitializeAsync(string newConversationTitle, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Conversation> OpenConversationAsync(Conversation conversation) => throw new NotSupportedException();
    public Task<Conversation> NewConversationAsync(string title) => throw new NotSupportedException();
    public Task<ChatRunResult> SendAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ChatRunResult> RetryAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ChatRunResult> RegenerateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CompactionResult> CompactAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public void Stop() { }
    public Task StopAndWaitAsync() => Task.CompletedTask;
    public bool TrySetMode(ChatMode mode) => false;
}
