using Lumi.Core.Tools;

namespace Lumi.Core.Reminders;

/// <summary>一次「跑 prompt」執行的狀態（M24，<c>scheduled_runs.status</c>）。</summary>
public enum ScheduledRunStatus
{
    /// <summary>已排入佇列或正在執行。</summary>
    Running,

    /// <summary>有工具超出授權範圍，暫停等使用者回答。</summary>
    AwaitingApproval,

    Completed,

    /// <summary>沒有完成（沒有模型、供應商錯誤…）。原因在 <see cref="ScheduledRun.FailureText"/>。</summary>
    Failed,

    /// <summary>Lumi 沒開，超過補跑寬限。沒有呼叫 AI，也沒有對話。</summary>
    Missed,

    /// <summary>執行中 Lumi 被關掉（或當機），不自動重跑。</summary>
    Interrupted,
}

public enum ScheduledRunTrigger
{
    /// <summary>照排程到點。</summary>
    Scheduled,

    /// <summary>錯過之後在寬限內補跑。</summary>
    CatchUp,

    /// <summary>使用者按「立即執行」。</summary>
    Manual,
}

/// <summary>
/// 一次執行的紀錄（<c>scheduled_runs</c> 一列）。<see cref="ScheduleTitle"/> 是當時排程名稱的快照；
/// <see cref="ConversationId"/> 指向 <c>kind = scheduled_run</c> 的對話，「錯過」沒有對話所以是 null。
/// </summary>
public sealed record ScheduledRun(
    string Id,
    string ScheduleId,
    string ScheduleTitle,
    string? ConversationId,
    DateTimeOffset PlannedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    ScheduledRunTrigger Trigger,
    ScheduledRunStatus Status,
    string? FailureText,
    DateTimeOffset? ReadAt)
{
    public static string NewId() => Guid.CreateVersion7().ToString();

    public bool IsUnread => ReadAt is null && Status != ScheduledRunStatus.Running;

    public bool IsActive => Status is ScheduledRunStatus.Running or ScheduledRunStatus.AwaitingApproval;
}

/// <summary>某個排程的一組紀錄（歷史抽屜「排程」分頁的一組）。<paramref name="Runs"/> 由新到舊。</summary>
public sealed record ScheduledRunGroup(string ScheduleId, string ScheduleTitle, IReadOnlyList<ScheduledRun> Runs);

public sealed class ScheduledRunOptions
{
    /// <summary>每個排程保留最近幾次紀錄；超過的最舊紀錄在每次執行結束後清掉。</summary>
    public int KeepRunsPerSchedule { get; set; } = 30;
}

/// <summary>排程執行紀錄的存取（SQLite 實作在 Infrastructure）。</summary>
public interface IScheduledRunStore
{
    Task AddAsync(ScheduledRun run, CancellationToken ct = default);

    /// <summary>以 Id 整筆換掉；找不到回 false。</summary>
    Task<bool> UpdateAsync(ScheduledRun run, CancellationToken ct = default);

    Task<ScheduledRun?> GetAsync(string runId, CancellationToken ct = default);

    Task<ScheduledRun?> GetByConversationAsync(string conversationId, CancellationToken ct = default);

    /// <summary>依排程分組；組內由新到舊（<see cref="ScheduledRun.PlannedAt"/>），每組最多 <paramref name="perScheduleLimit"/> 筆；組依最新一筆由新到舊。</summary>
    Task<IReadOnlyList<ScheduledRunGroup>> ListGroupedAsync(int perScheduleLimit, CancellationToken ct = default);

    /// <summary>某個排程的全部紀錄，由新到舊。</summary>
    Task<IReadOnlyList<ScheduledRun>> ListForScheduleAsync(string scheduleId, CancellationToken ct = default);

    /// <summary>未讀的筆數（<see cref="ScheduledRun.IsUnread"/>）。</summary>
    Task<int> CountUnreadAsync(CancellationToken ct = default);

    Task MarkReadAsync(string runId, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>某排程超過 <paramref name="keep"/> 筆的最舊紀錄（不含執行中與待確認的）。</summary>
    Task<IReadOnlyList<ScheduledRun>> ListBeyondAsync(string scheduleId, int keep, CancellationToken ct = default);

    /// <summary>刪除一筆紀錄的列（對話由呼叫端經 <c>ConversationDeletionService</c> 刪除）；找不到回 false。</summary>
    Task<bool> DeleteAsync(string runId, CancellationToken ct = default);

    /// <summary>啟動時呼叫：把執行中與待確認的改成 <see cref="ScheduledRunStatus.Interrupted"/>，回傳筆數。</summary>
    Task<int> MarkActiveInterruptedAsync(DateTimeOffset at, CancellationToken ct = default);

    /// <summary>啟動時呼叫：<c>kind = scheduled_run</c> 但沒有任何紀錄對應的對話 id（當機殘留）。</summary>
    Task<IReadOnlyList<string>> ListOrphanRunConversationsAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="ReminderService"/> 把到點的「跑 prompt」項目交給誰排隊執行（M24）。實作是 <c>ScheduledRunService</c>。
/// 沒有實作時（測試）用 <see cref="NullScheduledRunDispatcher"/>。
/// </summary>
public interface IScheduledRunDispatcher
{
    /// <summary>排入佇列後立即回來，不等執行完成。</summary>
    void Enqueue(ScheduledItem item, DateTimeOffset plannedAt, ScheduledRunTrigger trigger);

    /// <summary>記一筆「錯過」（沒有對話、不呼叫 AI）。</summary>
    Task RecordMissedAsync(ScheduledItem item, DateTimeOffset plannedAt, CancellationToken ct);
}

public sealed class NullScheduledRunDispatcher : IScheduledRunDispatcher
{
    public void Enqueue(ScheduledItem item, DateTimeOffset plannedAt, ScheduledRunTrigger trigger)
    {
    }

    public Task RecordMissedAsync(ScheduledItem item, DateTimeOffset plannedAt, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>目前正在執行的一次排程（用對話 id 找）。<paramref name="Grant"/> 是這次執行開始時的授權。</summary>
public sealed record ActiveScheduledRun(string RunId, string ScheduleId, string ScheduleTitle, ScheduleGrant Grant);

/// <summary>
/// 權限分流（<c>RoutingToolApprovalService</c>）與執行器之間的掛鉤（M24 4.3）。實作是 <c>ScheduledRunService</c>。
/// </summary>
public interface IScheduledRunHost
{
    /// <summary>這則對話是不是進行中的排程執行；不是回 null（一般對話）。</summary>
    ActiveScheduledRun? FindByConversation(string conversationId);

    /// <summary>
    /// 有工具超出授權：run 改 <see cref="ScheduledRunStatus.AwaitingApproval"/>、釋放執行位置（讓後面的排程先跑）、送「需要你確認」通知。
    /// 呼叫端等使用者回答期間持有回傳的物件；<c>DisposeAsync</c> 時重新排隊取得執行位置，並把 run 改回 <see cref="ScheduledRunStatus.Running"/>。
    /// </summary>
    Task<IAsyncDisposable> PauseForApprovalAsync(string conversationId, ToolApprovalRequest request, CancellationToken ct);

    /// <summary>「以後都允許」：把這個權限等級加進排程的授權（寫回 <c>action_json</c>）。</summary>
    Task AddGrantAsync(string scheduleId, ToolPermission permission, CancellationToken ct);
}

/// <summary>沒有排程執行器時：沒有任何對話是排程執行。</summary>
public sealed class NullScheduledRunHost : IScheduledRunHost
{
    public ActiveScheduledRun? FindByConversation(string conversationId) => null;

    public Task<IAsyncDisposable> PauseForApprovalAsync(string conversationId, ToolApprovalRequest request, CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable>(NoopDisposable.Instance);

    public Task AddGrantAsync(string scheduleId, ToolPermission permission, CancellationToken ct) => Task.CompletedTask;

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
