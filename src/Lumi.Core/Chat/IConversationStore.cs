namespace Lumi.Core.Chat;

/// <summary>
/// 對話與訊息的存取。只能新增訊息，不能修改或刪除：對話歷史只能往後加（agent.md 第 7 節）。
/// </summary>
public interface IConversationStore
{
    Task<Conversation> CreateAsync(string title, CancellationToken ct = default);

    /// <summary>最近更新的一則對話；一則都沒有時回 null。</summary>
    Task<Conversation?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>依送出順序排列。</summary>
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default);

    /// <summary>加在對話尾端，並更新對話的 <see cref="Conversation.UpdatedAt"/>。</summary>
    Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default);

    Task RenameAsync(string conversationId, string title, CancellationToken ct = default);
}
