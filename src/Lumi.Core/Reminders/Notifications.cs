namespace Lumi.Core.Reminders;

/// <summary>通知的種類。</summary>
public enum NotificationKind
{
    /// <summary>到點的提醒、計時器或排程。</summary>
    Reminder,

    /// <summary>啟動或睡眠喚醒後，彙總錯過的一次性提醒。</summary>
    MissedSummary,

    /// <summary>系統訊息（例如 MCP 伺服器連不上，M16）。</summary>
    SystemNotice,

    /// <summary>排程跑 prompt 的結果：完成、需要確認、失敗（M24）。帶 <see cref="NotificationTarget"/>。</summary>
    ScheduledRun,
}

/// <summary>通知被點「查看」時要開啟的東西（M24）。純資料，怎麼開由 App 決定。</summary>
public abstract record NotificationTarget;

/// <summary>開啟一筆排程執行紀錄（<see cref="ScheduledRun.Id"/>）。</summary>
public sealed record OpenScheduledRun(string RunId) : NotificationTarget;

/// <summary>要顯示給使用者的一則通知（純資料，怎麼顯示由 <see cref="INotificationSink"/> 的實作決定）。<paramref name="Target"/> 有值時多一顆「查看」。</summary>
public sealed record AppNotification(string Id, string Title, string Message, DateTimeOffset At, NotificationKind Kind, NotificationTarget? Target = null);

/// <summary>
/// 通知的送達。實作在 App（小精靈氣泡、工作台通知列）；Core 只定義介面。
/// 實作丟例外時，<see cref="ReminderService"/> 只記 log，不影響其他項目與下一次觸發。
/// </summary>
public interface INotificationSink
{
    Task ShowAsync(AppNotification notification, CancellationToken ct);
}

/// <summary>預設的通知送達：什麼都不做（E4 會用 App 的實作取代）。</summary>
public sealed class NullNotificationSink : INotificationSink
{
    public Task ShowAsync(AppNotification notification, CancellationToken ct) => Task.CompletedTask;
}
