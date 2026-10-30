using Lumi.Core.Usage;

namespace Lumi.Core.Tests.TestSupport;

/// <summary>記憶體版的用量紀錄（測試用）。<see cref="ListTopConversationsAsync"/> 沒有對話標題可查，回空清單。</summary>
internal sealed class InMemoryUsageStore : IUsageStore
{
    public List<UsageRecord> Records { get; } = [];

    public Task AddAsync(UsageRecord record, CancellationToken ct = default)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsageRecord>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UsageRecord>>(Records.Where(r => r.CreatedAt >= from && r.CreatedAt < to).OrderBy(r => r.CreatedAt).ToList());

    public Task<ConversationCost> GetConversationCostAsync(string conversationId, CancellationToken ct = default)
    {
        var mine = Records.Where(r => r.ConversationId == conversationId).ToList();
        if (mine.Count == 0)
            return Task.FromResult(ConversationCost.Empty);
        var priced = mine.Where(r => r.CostUsd is not null).ToList();
        return Task.FromResult(new ConversationCost(
            priced.Count == 0 ? null : priced.Sum(r => r.CostUsd!.Value),
            mine.Any(r => r.Estimated),
            mine.Count - priced.Count));
    }

    public Task<IReadOnlyList<ConversationCostItem>> ListTopConversationsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationCostItem>>([]);

    public Task<decimal> SumCostAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult(Records.Where(r => r.CreatedAt >= from && r.CreatedAt < to).Sum(r => r.CostUsd ?? 0m));
}
