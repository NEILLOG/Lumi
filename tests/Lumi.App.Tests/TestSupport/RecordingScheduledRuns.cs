using Lumi.App.ViewModels;
using Lumi.Core.Reminders;

namespace Lumi.App.Tests.TestSupport;

/// <summary>執行紀錄的記憶體服務：記下呼叫，事件由測試用 <see cref="Raise"/> 發出；RunNow 回傳 <see cref="NextRunNow"/>。</summary>
internal sealed class RecordingScheduledRunService : IScheduledRunService
{
    public event EventHandler<ScheduledRun>? RunUpdated;

    public event EventHandler<ScheduledRunEventArgs>? EventReceived;

    public List<string> RunNowCalls { get; } = [];

    public List<string> DeletedRuns { get; } = [];

    public List<string> DeletedSchedules { get; } = [];

    public ScheduledRun? NextRunNow { get; set; }

    public Task<ScheduledRun?> RunNowAsync(string scheduleId, CancellationToken ct = default)
    {
        RunNowCalls.Add(scheduleId);
        return Task.FromResult(NextRunNow);
    }

    public void Stop(string runId)
    {
    }

    public bool IsActive(string conversationId) => false;

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

    public Task MarkReadAsync(string runId, CancellationToken ct = default) => Task.CompletedTask;

    public void Raise(ScheduledRun run) => RunUpdated?.Invoke(this, run);

    public void Raise(ScheduledRunEventArgs args) => EventReceived?.Invoke(this, args);
}

/// <summary>打開紀錄的記錄（工作台的實作在 24F）。</summary>
internal sealed class RecordingScheduledRunViewer : IScheduledRunViewer
{
    public List<string> Opened { get; } = [];

    public Task OpenScheduledRunAsync(string runId)
    {
        Opened.Add(runId);
        return Task.CompletedTask;
    }
}
