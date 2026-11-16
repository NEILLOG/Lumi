using Lumi.Core.Chat;

namespace Lumi.Core.Branches;

public interface IBranchService
{
    /// <summary>開支線、帶回、放棄之後發（在呼叫者的執行緒上）。</summary>
    event EventHandler<BranchChangedArgs>? Changed;

    Task<BranchState> GetStateAsync(string conversationId, CancellationToken ct = default);

    /// <summary>不建立任何東西，只回答能不能從這則回答開支線（4.4 的表）。</summary>
    Task<BranchStartBlock> CheckStartAsync(string parentId, string forkMessageId, CancellationToken ct = default);

    /// <summary>建立支線並回傳它的對話（4.4）。不能開時丟 <see cref="InvalidOperationException"/>（訊息是 <see cref="BranchMessages.For"/>）。</summary>
    Task<Conversation> StartAsync(string parentId, string forkMessageId, string? quote, CancellationToken ct = default);

    /// <summary>整理摘要（4.5）。失敗回失敗的結果，不丟例外；被取消照常丟。</summary>
    Task<BranchSummaryDraft> DraftSummaryAsync(string conversationId, CancellationToken ct = default);

    /// <summary>帶回並回傳上一層的對話（4.5）。摘要空白或超過上限丟 <see cref="ArgumentException"/>；不是進行中的支線丟 <see cref="InvalidOperationException"/>。</summary>
    Task<Conversation> MergeAsync(string conversationId, string summary, CancellationToken ct = default);

    /// <summary>放棄並回傳上一層的對話（4.6）。不是進行中的支線丟 <see cref="InvalidOperationException"/>。</summary>
    Task<Conversation> AbandonAsync(string conversationId, CancellationToken ct = default);

    /// <summary>從這則對話往下找進行中的支線，回傳最深的那一則；沒有回 null。</summary>
    Task<Conversation?> FindActiveLeafAsync(string conversationId, CancellationToken ct = default);

    /// <summary>刪掉孤兒支線對話（E10）。失敗只記 log。</summary>
    Task CleanupOrphansAsync(CancellationToken ct = default);
}
