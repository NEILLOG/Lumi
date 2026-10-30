namespace Lumi.Core.Usage;

/// <summary>每次請求的用量與費用。紀錄只增不改；刪除對話、重新產生都不會讓已經花掉的費用消失。</summary>
public interface IUsageStore
{
    Task AddAsync(UsageRecord record, CancellationToken ct = default);

    /// <summary>[from, to) 之間的紀錄，依時間由舊到新（月報與匯出用；一個月的量在記憶體裡處理沒問題）。</summary>
    Task<IReadOnlyList<UsageRecord>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>一則對話全部的費用加總（含重新產生掉的那幾次）。</summary>
    Task<ConversationCost> GetConversationCostAsync(string conversationId, CancellationToken ct = default);

    /// <summary>[from, to) 之間費用最高的對話，只列還存在的對話，最多 <paramref name="limit"/> 則。</summary>
    Task<IReadOnlyList<ConversationCostItem>> ListTopConversationsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default);

    /// <summary>[from, to) 之間 cost_usd 的加總（null 不計）。預算檢查用，比 <see cref="ListAsync"/> 便宜。</summary>
    Task<decimal> SumCostAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <param name="CostUsd">有單價的請求加總；全部都沒有單價（或沒有紀錄）時是 null。</param>
/// <param name="UnpricedCount">沒有單價、因此沒計入的請求數。</param>
public sealed record ConversationCost(decimal? CostUsd, bool HasEstimated, int UnpricedCount)
{
    public static readonly ConversationCost Empty = new(null, false, 0);
}

/// <param name="TurnCount">這則對話目前的使用者訊息數。</param>
/// <param name="LastAt">這段期間內最後一筆紀錄的時間（顯示日期用）。</param>
public sealed record ConversationCostItem(string ConversationId, string Title, DateTimeOffset LastAt, int TurnCount, decimal CostUsd, bool HasEstimated);
