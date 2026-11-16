namespace Lumi.Core.Branches;

public interface IBranchStore
{
    /// <summary>新增一列。支線那則對話要先存在（外鍵）。同一個 id 已經有了丟 <see cref="InvalidOperationException"/>。</summary>
    Task AddAsync(Branch branch, CancellationToken ct = default);

    /// <summary>這則對話的支線資料；不是支線回 null。</summary>
    Task<Branch?> GetAsync(string conversationId, CancellationToken ct = default);

    /// <summary>直接從這則對話分出去的支線，依建立時間由舊到新。</summary>
    Task<IReadOnlyList<Branch>> ListChildrenAsync(string parentId, CancellationToken ct = default);

    /// <summary>這些主線底下的全部支線（兩層），依 (root_id, created_at) 排序。歷史抽屜用。</summary>
    Task<IReadOnlyList<Branch>> ListByRootsAsync(IReadOnlyCollection<string> rootIds, CancellationToken ct = default);

    /// <summary>狀態是 Active 時改成 Merged、記下摘要與結束時間；回傳有沒有改到。</summary>
    Task<bool> MergeAsync(string conversationId, string summary, DateTimeOffset endedAt, CancellationToken ct = default);

    /// <summary>狀態是 Active 時改成 Abandoned、記下結束時間；回傳有沒有改到。</summary>
    Task<bool> AbandonAsync(string conversationId, DateTimeOffset endedAt, CancellationToken ct = default);

    /// <summary>從這則對話分出去、已帶回、還沒交出的支線，依結束時間由舊到新。</summary>
    Task<IReadOnlyList<Branch>> ListUndeliveredAsync(string parentId, CancellationToken ct = default);

    /// <summary>記下摘要放進了哪一則訊息。已經有值的不覆蓋。</summary>
    Task MarkDeliveredAsync(IReadOnlyCollection<string> conversationIds, string messageId, CancellationToken ct = default);

    /// <summary><c>kind = 'branch'</c> 但沒有 <c>conversation_branches</c> 列的對話 id（建立到一半中斷留下的）。</summary>
    Task<IReadOnlyList<string>> ListOrphanConversationsAsync(CancellationToken ct = default);
}
