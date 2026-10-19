namespace Lumi.Core.Context;

public enum ContextKind { File, Folder, Image }

/// <summary>
/// 使用者加入這則對話的一個項目（輸入框上方的 chip）。<see cref="EmbeddedInMessageId"/> 是 null 代表還沒送出：
/// 下一則使用者訊息會帶上它當下的內容（快照）；之後不再重複送（M4 的 Q1）。
/// </summary>
public sealed record ContextItem(
    string Id,
    ContextKind Kind,
    string Path,
    string DisplayName,
    DateTimeOffset AddedAt,
    string? EmbeddedInMessageId)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public bool IsPending => EmbeddedInMessageId is null;
}

/// <summary>對話的上下文項目存取。移除項目＝從範圍拿掉；已送出的快照留在訊息歷史裡，不會被收回。</summary>
public interface IConversationContextStore
{
    Task<IReadOnlyList<ContextItem>> GetAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// 加入項目。同一個種類與路徑已經存在時不會重複：還沒送出的維持原樣；已送出的重新變成「待送出」
    /// （再加入同一個檔案＝再拍一次新快照），回傳的是現有的那一項。
    /// </summary>
    Task<ContextItem> AddAsync(string conversationId, ContextKind kind, string path, string displayName, CancellationToken ct = default);

    Task RemoveAsync(string conversationId, string itemId, CancellationToken ct = default);

    /// <summary>項目的內容已經嵌進這則訊息，之後不再重複送。</summary>
    Task MarkEmbeddedAsync(string conversationId, IReadOnlyCollection<string> itemIds, string messageId, CancellationToken ct = default);
}

/// <summary>沒有上下文儲存可用時（測試、最小組合）：永遠是空的。</summary>
public sealed class NullConversationContextStore : IConversationContextStore
{
    public Task<IReadOnlyList<ContextItem>> GetAsync(string conversationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContextItem>>([]);

    public Task<ContextItem> AddAsync(string conversationId, ContextKind kind, string path, string displayName, CancellationToken ct = default) =>
        throw new NotSupportedException("沒有可用的上下文儲存。");

    public Task RemoveAsync(string conversationId, string itemId, CancellationToken ct = default) => Task.CompletedTask;

    public Task MarkEmbeddedAsync(string conversationId, IReadOnlyCollection<string> itemIds, string messageId, CancellationToken ct = default) =>
        Task.CompletedTask;
}
