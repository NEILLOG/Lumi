using Lumi.Core.Chat;

namespace Lumi.Tools.Tests.Memory;

/// <summary>只有讀取訊息的假對話儲存：記憶工具判定來源時只會呼叫 <see cref="GetMessagesAsync"/>。</summary>
internal sealed class FakeChatHistory : IConversationStore
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public List<ChatMessage> Messages { get; } = [];

    /// <summary>若設定，<see cref="GetMessagesAsync"/> 會失敗（模擬讀取錯誤）。</summary>
    public Exception? Failure { get; set; }

    public static ChatMessage User(string text) => Message(ChatRole.User, text);

    public static ChatMessage Assistant(string text) => Message(ChatRole.Assistant, text);

    public static ChatMessage ToolResult(string text) => Message(ChatRole.Tool, text);

    private static ChatMessage Message(ChatRole role, string text) =>
        new(ChatMessage.NewId(), role, [new TextBlock(text)], Stamp);

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default)
    {
        if (Failure is not null)
            return Task.FromException<IReadOnlyList<ChatMessage>>(Failure);
        return Task.FromResult<IReadOnlyList<ChatMessage>>(Messages.ToList());
    }

    public Task<Conversation> CreateAsync(string title, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Conversation?> GetLatestAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => throw new NotSupportedException();
}
