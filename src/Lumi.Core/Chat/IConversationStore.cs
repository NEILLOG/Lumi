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
    /// 刪除整個對話：訊息、上下文項目隨外鍵一併刪除。不存在時回 false。
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
    /// 移除最後一則訊息（「重新產生」用）。從尾端移除不會讓前面任何一則的內容改變，所以不影響 thinking 簽章的一致性；
    /// 對話的更新時間不變，之後再加訊息時順序號接著剩下的最後一則。
    /// </summary>
    /// <returns>有移除回 true；對話本來就沒有訊息回 false。</returns>
    Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default);

    Task RenameAsync(string conversationId, string title, CancellationToken ct = default);
}
