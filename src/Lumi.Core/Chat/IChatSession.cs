namespace Lumi.Core.Chat;

public enum ChatRunKind { Send, Retry, Regenerate }

public sealed record ConversationChangedArgs(Conversation Conversation);

/// <summary><paramref name="UserText"/> 只有 <see cref="ChatRunKind.Send"/> 有值。</summary>
public sealed record RunStartedArgs(string ConversationId, ChatMode Mode, ChatRunKind Kind, string? UserText);

/// <summary>一輪回應的結局。<see cref="Failure"/> 與 <see cref="Unexpected"/> 不會同時出現；被使用者停止時兩者都沒有。</summary>
public sealed record ChatRunResult(string ConversationId, ChatFailure? Failure, bool Unexpected, bool Cancelled);

/// <summary>
/// 一輪回應結束。觀察者如果還有要等的收尾（例如從資料庫重讀訊息），用 <see cref="Defer"/> 登記，
/// 會話等它們全部做完才算結束（<see cref="IChatSession.StopAndWaitAsync"/> 也會等）。
/// </summary>
public sealed class RunFinishedArgs(ChatRunResult result)
{
    private readonly List<Task> _deferred = [];

    public ChatRunResult Result { get; } = result;

    public void Defer(Task work) => _deferred.Add(work);

    internal Task WhenAllDeferred() => Task.WhenAll(_deferred);
}

/// <summary>
/// 目前的對話與回應流程。工作台與小精靈共用同一個：誰送出的都一樣，每個 <see cref="ChatEvent"/> 依序發給所有觀察者，
/// 觀察者各自決定怎麼呈現（工作台更新訊息列表，小精靈更新氣泡與角色狀態）。
/// 事件在呼叫者的同步內容上發出（正式程式是 UI 執行緒）；觀察者丟例外只記 log，不會中斷回應。
/// </summary>
public interface IChatSession
{
    string? ConversationId { get; }

    ChatMode ActiveMode { get; }

    bool IsResponding { get; }

    /// <summary>切換或新對話。</summary>
    event EventHandler<ConversationChangedArgs>? ConversationChanged;

    /// <summary>回應開始，在第一個事件之前。</summary>
    event EventHandler<RunStartedArgs>? RunStarted;

    /// <summary>每個 <see cref="ChatEvent"/>，依序。</summary>
    event EventHandler<ChatEvent>? EventReceived;

    /// <summary>失敗、被停止或未預期的錯誤都會發；此時 <see cref="IsResponding"/> 已經是 false。</summary>
    event EventHandler<RunFinishedArgs>? RunFinished;

    event EventHandler<ChatMode>? ModeChanged;

    /// <summary>載入最近一則對話，沒有就建一則空的。</summary>
    Task<Conversation> InitializeAsync(string newConversationTitle, CancellationToken ct = default);

    /// <summary>回應中先停止再切換。</summary>
    Task<Conversation> OpenConversationAsync(Conversation conversation);

    /// <summary>回應中先停止再建立。</summary>
    Task<Conversation> NewConversationAsync(string title);

    /// <summary>用目前的模式送出。已經在回應中、或還沒有對話時丟 <see cref="InvalidOperationException"/>。</summary>
    Task<ChatRunResult> SendAsync(string text, CancellationToken ct = default);

    /// <summary>不新增使用者訊息，用目前的歷史再要一次回應。</summary>
    Task<ChatRunResult> RetryAsync(CancellationToken ct = default);

    /// <summary>移除最後一輪助理回應後再要一次（畫面上的移除由觀察者在 <see cref="RunStarted"/> 做）。</summary>
    Task<ChatRunResult> RegenerateAsync(CancellationToken ct = default);

    /// <summary>
    /// 手動整理上下文（M22）：直接做摘要（第二層），不看門檻。整理期間 <see cref="IsResponding"/> 為 true（不能送出、重試或重新產生），
    /// <see cref="StopAndWaitAsync"/> 會取消它。只發 <see cref="CompactionStarted"/>、<see cref="CompactionCompleted"/>、<see cref="CompactionFailed"/>
    /// 與摘要呼叫的 <see cref="UsageEvent"/> 這幾種事件（<see cref="EventReceived"/>），不發 <see cref="RunStarted"/>／<see cref="RunFinished"/>。
    /// 回應進行中或還沒有對話時丟 <see cref="InvalidOperationException"/>；被停止時回傳沒有整理、也沒有失敗的結果（此時只有 <see cref="CompactionStarted"/>，沒有結束事件）；其他例外轉成失敗結果。
    /// </summary>
    Task<CompactionResult> CompactAsync(CancellationToken ct = default);

    void Stop();

    /// <summary>取消目前的回應，並等收尾全部做完才回來；沒有回應進行中就直接回來。</summary>
    Task StopAndWaitAsync();

    /// <summary>回應中不能切換（ui-spec 2.2），回傳 false。</summary>
    bool TrySetMode(ChatMode mode);
}
