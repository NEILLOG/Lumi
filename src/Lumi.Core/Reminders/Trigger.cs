using System.Text.Json.Serialization;
using Lumi.Core.Context;

namespace Lumi.Core.Reminders;

/// <summary>
/// 觸發條件。序列化時以 <c>type</c> 欄位區分（<c>one_time</c>、<c>recurrence</c>），欄位名稱與舊版 schedules.json 相同。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OneTimeTrigger), "one_time")]
[JsonDerivedType(typeof(RecurrenceTrigger), "recurrence")]
public abstract record Trigger;

/// <summary>一次性：在 <paramref name="At"/> 觸發一次。</summary>
public sealed record OneTimeTrigger(DateTimeOffset At) : Trigger;

/// <summary>
/// 週期：依項目的時區，在當地時間 <paramref name="TimeOfDay"/> 觸發。
/// <paramref name="Days"/> 只給 <see cref="RecurrenceKind.Weekly"/> 用（null 或空＝取計算當下那天的星期）；
/// <paramref name="DayOfMonth"/> 只給 <see cref="RecurrenceKind.Monthly"/> 用（null＝1）。
/// </summary>
public sealed record RecurrenceTrigger(
    RecurrenceKind Kind,
    TimeOnly TimeOfDay,
    IReadOnlyList<DayOfWeek>? Days,
    int? DayOfMonth) : Trigger;

/// <summary>觸發時要做的事：跳通知（<c>notify</c>），或用指定的 prompt 跑一次對話迴圈（<c>run_prompt</c>，M24）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NotifyAction), "notify")]
[JsonDerivedType(typeof(RunPromptAction), "run_prompt")]
public abstract record ScheduledAction;

/// <summary>跳出通知（顯示標題與訊息）。</summary>
public sealed record NotifyAction : ScheduledAction;

/// <summary>
/// 到點時用 <paramref name="Prompt"/> 跑一次對話迴圈（M24）。<paramref name="Grant"/> 是建立時一次授權的權限等級，
/// <paramref name="Scope"/> 是這個排程的工作範圍（建立當下那則對話加入的檔案與資料夾；沒有就是空的）。
/// </summary>
public sealed record RunPromptAction(string Prompt, ScheduleGrant Grant, IReadOnlyList<ScheduleScopeItem> Scope) : ScheduledAction;

/// <summary>
/// 排程執行時不再詢問的權限等級（<see cref="Tools.ToolPermission.ReadOnly"/> 本來就不詢問，不必記）。
/// 記憶、上傳、管理排程等工具不受這份授權涵蓋（見 <c>ScheduledRunApprovalPolicy</c>）。
/// </summary>
public sealed record ScheduleGrant(bool Write, bool Network, bool Destructive)
{
    public static readonly ScheduleGrant None = new(false, false, false);

    /// <summary>這個權限等級有沒有被授權；<c>ReadOnly</c> 一律 true。</summary>
    public bool Allows(Tools.ToolPermission permission) => permission switch
    {
        Tools.ToolPermission.ReadOnly => true,
        Tools.ToolPermission.Write => Write,
        Tools.ToolPermission.Network => Network,
        Tools.ToolPermission.Destructive => Destructive,
        _ => false,
    };

    /// <summary>加上一個權限等級後的新授權（「以後都允許」用）。</summary>
    public ScheduleGrant With(Tools.ToolPermission permission) => permission switch
    {
        Tools.ToolPermission.Write => this with { Write = true },
        Tools.ToolPermission.Network => this with { Network = true },
        Tools.ToolPermission.Destructive => this with { Destructive = true },
        _ => this,
    };
}

/// <summary>排程的工作範圍裡的一個項目；只有檔案與資料夾（<see cref="ContextKind.File"/>、<see cref="ContextKind.Folder"/>）。</summary>
public sealed record ScheduleScopeItem(ContextKind Kind, string Path, string DisplayName);
