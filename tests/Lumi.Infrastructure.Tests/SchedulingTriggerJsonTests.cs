using System.Globalization;
using System.Text.Json;
using Lumi.Core.Reminders;

namespace Lumi.Infrastructure.Tests;

/// <summary>
/// 觸發條件與動作的 JSON 形狀要與舊版 schedules.json 相同（plan/09-convert-from-old.md 8.3、8.4），
/// 舊版的檔案內容要能直接讀進來（M13 匯入）。
/// </summary>
public sealed class SchedulingTriggerJsonTests
{
    private static DateTimeOffset T(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>
    /// 比較序列化後的樣子。<c>Days</c> 是 <c>IReadOnlyList</c>，record 的相等比較是比參考，反序列化出來的一定不相等，
    /// 所以比內容（JSON）。
    /// </summary>
    private static void AssertSameTrigger(Trigger expected, Trigger? actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    [Fact]
    public void OneTime_SerializesWithTypeOneTimeAndAtField()
    {
        var json = JsonSerializer.Serialize<Trigger>(new OneTimeTrigger(T("2026-10-08T09:00:00+08:00")));

        Assert.Contains("\"type\":\"one_time\"", json);
        Assert.Contains("\"At\":\"2026-10-08T09:00:00+08:00\"", json);
    }

    [Fact]
    public void Recurrence_SerializesTimeOfDayAsHhMmSs_AndNullsAsNull()
    {
        var json = JsonSerializer.Serialize<Trigger>(new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(10, 0), null, null));

        Assert.Contains("\"type\":\"recurrence\"", json);
        Assert.Contains("\"Kind\":0", json);
        Assert.Contains("\"TimeOfDay\":\"10:00:00\"", json);
        Assert.Contains("\"Days\":null", json);
        Assert.Contains("\"DayOfMonth\":null", json);
    }

    [Fact]
    public void Recurrence_WeeklyDays_AreNumbersLikeTheOldFormat()
    {
        var json = JsonSerializer.Serialize<Trigger>(new RecurrenceTrigger(RecurrenceKind.Weekly, new TimeOnly(9, 0), [DayOfWeek.Sunday, DayOfWeek.Friday], null));

        Assert.Contains("\"Days\":[0,5]", json);
    }

    [Fact]
    public void NotifyAction_SerializesAsTypeNotify() =>
        Assert.Equal("{\"type\":\"notify\"}", JsonSerializer.Serialize<ScheduledAction>(new NotifyAction()));

    [Fact]
    public void Trigger_RoundTrips_ForEveryShape()
    {
        var triggers = new Trigger[]
        {
            new OneTimeTrigger(T("2026-10-08T09:00:00+08:00")),
            new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(10, 0, 0), null, null),
            new RecurrenceTrigger(RecurrenceKind.Weekdays, new TimeOnly(8, 15, 30), null, null),
            new RecurrenceTrigger(RecurrenceKind.Weekly, new TimeOnly(18, 30), [DayOfWeek.Monday, DayOfWeek.Friday], null),
            new RecurrenceTrigger(RecurrenceKind.Monthly, new TimeOnly(0, 0), null, 31),
        };

        foreach (var trigger in triggers)
        {
            var json = JsonSerializer.Serialize(trigger);

            AssertSameTrigger(trigger, JsonSerializer.Deserialize<Trigger>(json));
        }
    }

    [Fact]
    public void OldFormat_OneTimeSample_Deserializes()
    {
        // plan/09-convert-from-old.md 8.6 的第一筆（Trigger 與 Action 部分）
        const string trigger = """{ "type": "one_time", "At": "2026-10-08T09:00:00+08:00" }""";
        const string action = """{ "type": "notify" }""";

        var parsedTrigger = JsonSerializer.Deserialize<Trigger>(trigger);
        var parsedAction = JsonSerializer.Deserialize<ScheduledAction>(action);

        Assert.Equal(new OneTimeTrigger(T("2026-10-08T09:00:00+08:00")), parsedTrigger);
        Assert.IsType<NotifyAction>(parsedAction);
    }

    [Fact]
    public void OldFormat_RecurrenceSample_Deserializes()
    {
        // 8.6 的第二筆：每天 10:00
        const string trigger = """{ "type": "recurrence", "Kind": 0, "TimeOfDay": "10:00:00", "Days": null, "DayOfMonth": null }""";

        var parsed = JsonSerializer.Deserialize<Trigger>(trigger);

        AssertSameTrigger(new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(10, 0), null, null), parsed);
    }

    [Fact]
    public void OldFormat_WeeklyAndMonthly_Deserialize()
    {
        const string weekly = """{ "type": "recurrence", "Kind": 2, "TimeOfDay": "09:30:00", "Days": [1, 5], "DayOfMonth": null }""";
        const string monthly = """{ "type": "recurrence", "Kind": 3, "TimeOfDay": "07:00:00", "Days": null, "DayOfMonth": 15 }""";

        var parsedWeekly = JsonSerializer.Deserialize<Trigger>(weekly);
        var parsedMonthly = JsonSerializer.Deserialize<Trigger>(monthly);

        AssertSameTrigger(new RecurrenceTrigger(RecurrenceKind.Weekly, new TimeOnly(9, 30), [DayOfWeek.Monday, DayOfWeek.Friday], null), parsedWeekly);
        AssertSameTrigger(new RecurrenceTrigger(RecurrenceKind.Monthly, new TimeOnly(7, 0), null, 15), parsedMonthly);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, ((RecurrenceTrigger)parsedWeekly!).Days);
    }

    [Fact]
    public void UnknownTriggerType_ThrowsJsonException_SoTheStoreCanSkipTheRow() =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Trigger>("""{ "type": "future_kind" }"""));
}
