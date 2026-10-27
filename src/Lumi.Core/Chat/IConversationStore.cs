namespace Lumi.Core.Chat;

/// <summary>
/// 對話與訊息的存取。對話歷史只能往後加，或從尾端移除（agent.md 第 7 節）：不能修改、也不能刪除中間的訊息。
/// </summary>
public interface IConversationStore
{
    Task<Conversation> CreateAsync(string title, CancellationToken ct = default);

    /// <summary>最近更新的一則對話；一則都沒有時回 null。</summary>
    Task<Conversation?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>有訊息的對話，依更新時間由新到舊。沒有任何訊息的對話不列出。</summary>
    Task<IReadOnlyList<Conversation>> ListAsync(int limit, CancellationToken ct = default) => ListAsync(limit, null, ct);

    /// <summary>
    /// 有訊息的對話，依 (更新時間, id) 由新到舊，取 <paramref name="before"/> 之後的 <paramref name="limit"/> 則。
    /// <paramref name="before"/> 是上一頁最後一則（游標）；null 表示第一頁。
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default);

    /// <summary>
    /// 刪除整個對話：訊息、上下文項目、整理紀錄隨外鍵一併刪除。不存在時回 false。
    /// 附件檔案不在這裡處理（由 <c>ConversationDeletionService</c> 在刪除之後清理）。
    /// </summary>
    Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// 比對標題與訊息的文字內容（不分大小寫）；每筆附命中片段。查詢字串去頭尾空白，空白回空清單。
    /// 結果依更新時間由新到舊，最多 <paramref name="limit"/> 筆。
    /// </summary>
    Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default);

    /// <summary>依送出順序排列。</summary>
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default);

    /// <summary>加在對話尾端，並更新對話的 <see cref="Conversation.UpdatedAt"/>。</summary>
    Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default);

    /// <summary>
    /// 記下送出這則訊息時附在後面的模式指示（<see cref="ChatMessage.TurnSuffix"/>）。只在它還是對話最後一則時呼叫
    /// （之後的訊息已經綁在它上面，不能再改）；訊息不存在就什麼都不做。預設什麼都不做，只有真的存檔的實作需要覆寫。
    /// </summary>
    Task SetTurnSuffixAsync(string conversationId, string messageId, string? turnSuffix, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 這則對話第一次送出時用的 system prompt；之後整則對話都送它（<see cref="Prompts.SystemPromptUpdate"/>）。還沒記過回 null。
    /// 預設不存（永遠回 null），只有真的存檔的實作需要覆寫。
    /// </summary>
    Task<string?> GetSystemPromptAsync(string conversationId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>記下這則對話的 system prompt。只在第一次送出時呼叫一次，之後不再改。預設什麼都不做。</summary>
    Task SetSystemPromptAsync(string conversationId, string systemPrompt, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 這則對話第一次送出時凍結的工具定義（<see cref="Tools.ToolSnapshotJson"/> 的 JSON）；之後整則對話都送它（M15 D1）。還沒記過回 null。
    /// 預設不存（永遠回 null），只有真的存檔的實作需要覆寫。
    /// </summary>
    Task<string?> GetToolSnapshotAsync(string conversationId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>記下這則對話的工具快照。只在第一次送出時呼叫一次。預設什麼都不做。</summary>
    Task SetToolSnapshotAsync(string conversationId, string snapshotJson, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 存一筆上下文整理紀錄（M22）。儲存實作順便記下當下對話最後一則訊息的順序號
    /// （<see cref="CompactionState.AppliedAtSeq"/>）。預設什麼都不做，只有真的存檔的實作需要覆寫。
    /// </summary>
    Task AddCompactionAsync(string conversationId, CompactionRecord record, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>這則對話目前的整理狀態（第一層、第二層各取最新一筆）。預設回空狀態。</summary>
    Task<CompactionState> GetCompactionStateAsync(string conversationId, CancellationToken ct = default) =>
        Task.FromResult(CompactionState.Empty);

    /// <summary>這則對話所有摘要呼叫的 token 用量加總（計入本對話累計）。預設全 0。</summary>
    Task<TokenUsage> GetCompactionUsageAsync(string conversationId, CancellationToken ct = default) => Task.FromResult(TokenUsage.Zero);

    /// <summary>
    /// 移除最後一則訊息（「重新產生」用）。從尾端移除不會讓前面任何一則的內容改變，所以不影響 thinking 簽章的一致性；
    /// 對話的更新時間不變，之後再加訊息時順序號接著剩下的最後一則。
    /// </summary>
    /// <returns>有移除回 true；對話本來就沒有訊息回 false。</returns>
    Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default);

    Task RenameAsync(string conversationId, string title, CancellationToken ct = default);

    /// <summary>
    /// 從 <paramref name="since"/>（含）起，所有對話的助理訊息 token 加總（設定首頁的本月／今天用量）。
    /// 預設全 0，只有真的存檔的實作需要覆寫。
    /// </summary>
    Task<TokenTotals> GetTokenTotalsAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(TokenTotals.Zero);

    /// <summary>
    /// 跨對話最近的工具結果，由新到舊，最多 <paramref name="limit"/> 筆（設定首頁的「最近的動作」）。
    /// 預設空清單，只有真的存檔的實作需要覆寫。
    /// </summary>
    Task<IReadOnlyList<RecentToolAction>> ListRecentToolActionsAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecentToolAction>>([]);
}
