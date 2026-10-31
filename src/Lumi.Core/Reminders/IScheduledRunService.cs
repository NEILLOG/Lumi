using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.Core.Reminders;

/// <summary>排程執行中收到的一個對話事件（紀錄檢視即時更新用）。</summary>
public sealed record ScheduledRunEventArgs(string RunId, string ConversationId, ChatEvent Event);

/// <summary>
/// 排程跑 prompt 的執行器對 UI 的介面（M24，實作 <c>ScheduledRunService</c>，同時也是 <see cref="IScheduledRunDispatcher"/> 與 <see cref="IScheduledRunHost"/>）。
/// 事件在背景執行緒發出，UI 要自己切回 UI 執行緒。
/// </summary>
public interface IScheduledRunService
{
    /// <summary>某筆紀錄的狀態改變（含新建立）。</summary>
    event EventHandler<ScheduledRun>? RunUpdated;

    /// <summary>執行中的每個對話事件，依序。</summary>
    event EventHandler<ScheduledRunEventArgs>? EventReceived;

    /// <summary>立即執行（不改變下次排定時間；停用的排程也可以）。排程不存在回 null。</summary>
    Task<ScheduledRun?> RunNowAsync(string scheduleId, CancellationToken ct = default);

    /// <summary>停止進行中的執行（紀錄改成 <see cref="ScheduledRunStatus.Interrupted"/>）。</summary>
    void Stop(string runId);

    /// <summary>這則對話是不是進行中（執行中或待確認）的排程執行；進行中的紀錄不能刪除。</summary>
    bool IsActive(string conversationId);

    /// <summary>刪除一筆紀錄與它的對話（經 <c>ConversationDeletionService</c>）；進行中的回 false。</summary>
    Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default);

    /// <summary>刪除某排程的全部紀錄（排程被刪、使用者選擇一併刪除時用）；進行中的略過。回傳刪除筆數。</summary>
    Task<int> DeleteRunsForScheduleAsync(string scheduleId, CancellationToken ct = default);

    /// <summary>
    /// 這筆執行正在等使用者確認的工具請求（待確認時打開紀錄，要能補出確認按鈕）；沒有等待中的請求回 null。
    /// 預設回 null，只有真的執行排程的實作需要覆寫。
    /// </summary>
    ToolApprovalRequest? GetPendingApproval(string runId) => null;

    /// <summary>標已讀（打開紀錄時）。</summary>
    Task MarkReadAsync(string runId, CancellationToken ct = default);
}
