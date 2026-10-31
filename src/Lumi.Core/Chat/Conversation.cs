namespace Lumi.Core.Chat;

/// <summary>對話的種類（M24）：一般對話，或排程執行的紀錄。歷史清單、搜尋、啟動時載入的最近對話只看 <see cref="Chat"/>。</summary>
public enum ConversationKind { Chat, ScheduledRun }

public sealed record Conversation(
    string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ConversationKind Kind = ConversationKind.Chat)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}

/// <summary>
/// 分頁游標：上一頁最後一則對話的 (更新時間, id)。相同更新時間時靠 id 分出先後，所以不會重複也不會漏掉。
/// </summary>
public sealed record ConversationCursor(DateTimeOffset UpdatedAt, string Id);

/// <summary>搜尋結果：對話本身，加上命中的片段（只有訊息內文命中時才有；只有標題命中時是空字串）。</summary>
public sealed record ConversationSearchHit(Conversation Conversation, string Snippet);
