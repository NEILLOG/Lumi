using System.Text.Json.Serialization;

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

/// <summary>觸發時要做的事。目前只有通知；之後可加 run_prompt（舊版有）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NotifyAction), "notify")]
public abstract record ScheduledAction;

/// <summary>跳出通知（顯示標題與訊息）。</summary>
public sealed record NotifyAction : ScheduledAction;
