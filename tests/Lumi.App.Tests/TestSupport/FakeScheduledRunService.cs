using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.App.Tests.TestSupport;

/// <summary><see cref="IScheduledRunService"/> 的假實作（App 測試用）：記下呼叫，事件由測試用 <see cref="Raise"/> 發出。</summary>
internal sealed class FakeScheduledRunService : IScheduledRunService
{
    public event EventHandler<ScheduledRun>? RunUpdated;

    public event EventHandler<ScheduledRunEventArgs>? EventReceived;

    public List<string> RunNowCalls { get; } = [];

    public List<string> StopCalls { get; } = [];

    public List<string> DeletedRuns { get; } = [];

    public List<string> DeletedSchedules { get; } = [];

    public List<string> ReadRuns { get; } = [];

    public HashSet<string> ActiveConversations { get; } = [];

    /// <summary>待確認的請求（依紀錄 id）；<see cref="GetPendingApproval"/> 回傳。</summary>
    public Dictionary<string, ToolApprovalRequest> Pending { get; } = [];

    public ToolApprovalRequest? GetPendingApproval(string runId) => Pending.GetValueOrDefault(runId);

    public Task<ScheduledRun?> RunNowAsync(string scheduleId, CancellationToken ct = default)
    {
        RunNowCalls.Add(scheduleId);
        return Task.FromResult<ScheduledRun?>(null);
    }

    public void Stop(string runId) => StopCalls.Add(runId);

    public bool IsActive(string conversationId) => ActiveConversations.Contains(conversationId);

    public Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default)
    {
        DeletedRuns.Add(runId);
        return Task.FromResult(true);
    }

    public Task<int> DeleteRunsForScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        DeletedSchedules.Add(scheduleId);
        return Task.FromResult(0);
    }

    public Task MarkReadAsync(string runId, CancellationToken ct = default)
    {
        ReadRuns.Add(runId);
        return Task.CompletedTask;
    }

    public void Raise(ScheduledRun run) => RunUpdated?.Invoke(this, run);

    public void Raise(ScheduledRunEventArgs args) => EventReceived?.Invoke(this, args);
}
