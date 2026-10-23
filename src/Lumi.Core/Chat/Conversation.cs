namespace Lumi.Core.Chat;

public sealed record Conversation(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}

/// <summary>
/// 分頁游標：上一頁最後一則對話的 (更新時間, id)。相同更新時間時靠 id 分出先後，所以不會重複也不會漏掉。
/// </summary>
public sealed record ConversationCursor(DateTimeOffset UpdatedAt, string Id);

/// <summary>搜尋結果：對話本身，加上命中的片段（只有訊息內文命中時才有；只有標題命中時是空字串）。</summary>
public sealed record ConversationSearchHit(Conversation Conversation, string Snippet);
