using Lumi.Core.Usage;

namespace Lumi.App.Tests.TestSupport;

/// <summary>記憶體版的提示紀錄（測試用）。</summary>
internal sealed class InMemoryUsageHintLog : IUsageHintLog
{
    public List<UsageHintEntry> Entries { get; } = [];

    public Task AddAsync(UsageHintEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task SetOutcomeAsync(string id, UsageHintOutcome outcome, CancellationToken ct = default)
    {
        var index = Entries.FindIndex(e => e.Id == id);
        if (index >= 0)
            Entries[index] = Entries[index] with { Outcome = outcome };
        return Task.CompletedTask;
    }

    public Task<bool> WasShownAsync(string rule, string conversationId, CancellationToken ct = default) =>
        Task.FromResult(Entries.Any(e => e.Rule == rule && e.ConversationId == conversationId && e.Outcome != UsageHintOutcome.Suppressed));

    public Task<DateTimeOffset?> LastShownAsync(string rule, CancellationToken ct = default) =>
        Task.FromResult(Entries.Where(e => e.Rule == rule && e.Outcome != UsageHintOutcome.Suppressed).Select(e => (DateTimeOffset?)e.CreatedAt).Max());

    public Task<IReadOnlyList<UsageHintEntry>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UsageHintEntry>>(Entries.Where(e => e.CreatedAt >= from && e.CreatedAt < to).ToList());
}
