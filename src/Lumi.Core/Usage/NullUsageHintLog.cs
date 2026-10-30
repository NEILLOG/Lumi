namespace Lumi.Core.Usage;

/// <summary>沒有資料庫時的預設（測試與尚未接上儲存的場合）：什麼都不記、查什麼都是空的。</summary>
public sealed class NullUsageHintLog : IUsageHintLog
{
    public Task AddAsync(UsageHintEntry entry, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetOutcomeAsync(string id, UsageHintOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> WasShownAsync(string rule, string conversationId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<DateTimeOffset?> LastShownAsync(string rule, CancellationToken ct = default) => Task.FromResult<DateTimeOffset?>(null);

    public Task<IReadOnlyList<UsageHintEntry>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UsageHintEntry>>([]);
}
