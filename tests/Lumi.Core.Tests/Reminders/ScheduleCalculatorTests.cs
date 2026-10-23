using System.Globalization;
using Lumi.Core.Reminders;

namespace Lumi.Core.Tests.Reminders;

public sealed class ScheduleCalculatorTests
{
    private const string Taipei = "Asia/Taipei";
    private const string NewYork = "America/New_York";
    private const string London = "Europe/London";

    private static DateTimeOffset T(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static Trigger Daily(string time) =>
        new RecurrenceTrigger(RecurrenceKind.Daily, TimeOnly.Parse(time, CultureInfo.InvariantCulture), null, null);

    private static Trigger Weekdays(string time) =>
        new RecurrenceTrigger(RecurrenceKind.Weekdays, TimeOnly.Parse(time, CultureInfo.InvariantCulture), null, null);

    private static Trigger Weekly(string time, params DayOfWeek[] days) =>
        new RecurrenceTrigger(RecurrenceKind.Weekly, TimeOnly.Parse(time, CultureInfo.InvariantCulture), days, null);

    private static Trigger Monthly(string time, int? dayOfMonth) =>
        new RecurrenceTrigger(RecurrenceKind.Monthly, TimeOnly.Parse(time, CultureInfo.InvariantCulture), null, dayOfMonth);

    private static DateTimeOffset? Next(Trigger trigger, string tz, string after) =>
        ScheduleCalculator.NextOccurrence(trigger, tz, T(after));

    [Fact]
    public void OneTime_LaterThanAfter_ReturnsAt() =>
        Assert.Equal(T("2026-10-25T09:00+08:00"), Next(new OneTimeTrigger(T("2026-10-25T09:00+08:00")), Taipei, "2026-10-23T08:00+08:00"));

    [Fact]
    public void OneTime_EqualToAfter_ReturnsNull() =>
        Assert.Null(Next(new OneTimeTrigger(T("2026-10-25T09:00+08:00")), Taipei, "2026-10-25T09:00+08:00"));

    [Fact]
    public void OneTime_BeforeAfter_ReturnsNull() =>
        Assert.Null(Next(new OneTimeTrigger(T("2026-10-23T09:00+08:00")), Taipei, "2026-10-25T09:00+08:00"));

    [Fact]
    public void OneTime_IgnoresTimeZone_BecauseAtIsAlreadyAnInstant()
    {
        var at = T("2026-10-25T09:00+08:00");

        Assert.Equal(at, Next(new OneTimeTrigger(at), NewYork, "2026-10-23T00:00Z"));
    }

    [Fact]
    public void Daily_BeforeTimeOfDay_FiresTheSameDay()
    {
        Assert.Equal(T("2026-10-23T09:00+08:00"), Next(Daily("09:00"), Taipei, "2026-10-23T08:00+08:00"));
    }

    [Fact]
    public void Daily_ExactlyAtTimeOfDay_ReturnsTheNextDay()
    {
        // after 剛好等於觸發時間：要取下一次（嚴格晚於）
        Assert.Equal(T("2026-10-24T09:00+08:00"), Next(Daily("09:00"), Taipei, "2026-10-23T09:00+08:00"));
    }

    [Fact]
    public void Daily_AfterTimeOfDay_ReturnsTheNextDay()
    {
        Assert.Equal(T("2026-10-24T09:00+08:00"), Next(Daily("09:00"), Taipei, "2026-10-23T09:00:01+08:00"));
    }

    [Fact]
    public void Daily_AtMidnight_FromLateEveningBeforeIt_ReturnsNextMidnight()
    {
        Assert.Equal(T("2026-10-24T00:00+08:00"), Next(Daily("00:00"), Taipei, "2026-10-23T23:59+08:00"));
    }

    [Fact]
    public void Daily_At2359_FromEarlyMorning_ReturnsSameDay()
    {
        Assert.Equal(T("2026-10-23T23:59+08:00"), Next(Daily("23:59"), Taipei, "2026-10-23T00:00+08:00"));
    }

    [Fact]
    public void Daily_AcrossNewYear_From2359On1231_ReturnsMidnightOfJan1()
    {
        Assert.Equal(T("2027-01-01T00:00+08:00"), Next(Daily("00:00"), Taipei, "2026-12-31T23:59+08:00"));
    }

    [Fact]
    public void Weekdays_FromFridayAfternoon_SkipsTheWeekend()
    {
        // 2026-10-23 是週五
        Assert.Equal(T("2026-10-26T09:00+08:00"), Next(Weekdays("09:00"), Taipei, "2026-10-23T10:00+08:00"));
    }

    [Fact]
    public void Weekdays_FromSaturday_ReturnsMonday()
    {
        Assert.Equal(T("2026-10-26T09:00+08:00"), Next(Weekdays("09:00"), Taipei, "2026-10-24T12:00+08:00"));
    }

    [Fact]
    public void Weekdays_FromSunday_ReturnsMonday()
    {
        Assert.Equal(T("2026-10-26T09:00+08:00"), Next(Weekdays("09:00"), Taipei, "2026-10-25T12:00+08:00"));
    }

    [Fact]
    public void Weekdays_OnFridayAfterTheTime_ReturnsMonday()
    {
        Assert.Equal(T("2026-10-26T09:00+08:00"), Next(Weekdays("09:00"), Taipei, "2026-10-23T09:00+08:00"));
    }

    [Fact]
    public void Weekly_MultipleDays_FromTuesday_ReturnsTheNextListedDay()
    {
        // 週一、週五；2026-10-20 是週二
        Assert.Equal(T("2026-10-23T09:00+08:00"), Next(Weekly("09:00", DayOfWeek.Friday, DayOfWeek.Monday), Taipei, "2026-10-20T10:00+08:00"));
    }

    [Fact]
    public void Weekly_MultipleDays_AfterOneListedDay_ReturnsTheNextOne()
    {
        Assert.Equal(T("2026-10-26T09:00+08:00"), Next(Weekly("09:00", DayOfWeek.Friday, DayOfWeek.Monday), Taipei, "2026-10-23T09:00+08:00"));
    }

    [Fact]
    public void Weekly_Sunday_FromSaturday_ReturnsSunday()
    {
        Assert.Equal(T("2026-10-25T09:00+08:00"), Next(Weekly("09:00", DayOfWeek.Sunday), Taipei, "2026-10-24T20:00+08:00"));
    }

    [Fact]
    public void Weekly_Sunday_ExactlyAtTime_ReturnsTheSundayAfter()
    {
        Assert.Equal(T("2026-11-01T09:00+08:00"), Next(Weekly("09:00", DayOfWeek.Sunday), Taipei, "2026-10-25T09:00+08:00"));
    }

    [Fact]
    public void Weekly_NullDays_UsesTheWeekdayOfAfter()
    {
        // after 是 2026-10-21（週三）的 10:00，時間點 09:00 已過 → 下一個週三
        var trigger = new RecurrenceTrigger(RecurrenceKind.Weekly, new TimeOnly(9, 0), null, null);

        Assert.Equal(T("2026-10-28T09:00+08:00"), Next(trigger, Taipei, "2026-10-21T10:00+08:00"));
    }

    [Fact]
    public void Weekly_EmptyDays_BehavesLikeNullDays()
    {
        var trigger = new RecurrenceTrigger(RecurrenceKind.Weekly, new TimeOnly(9, 0), [], null);

        Assert.Equal(T("2026-10-28T09:00+08:00"), Next(trigger, Taipei, "2026-10-21T10:00+08:00"));
    }

    [Fact]
    public void Weekly_DuplicateDays_DoNotChangeTheResult()
    {
        // 2026-10-20 是週二，下一個週一是 10-26；重複的週一不會多出一次
        Assert.Equal(T("2026-10-26T09:00+08:00"), Next(Weekly("09:00", DayOfWeek.Monday, DayOfWeek.Monday), Taipei, "2026-10-20T10:00+08:00"));
    }

    [Fact]
    public void Monthly_NullDayOfMonth_DefaultsToTheFirst()
    {
        Assert.Equal(T("2026-11-01T09:00+08:00"), Next(Monthly("09:00", null), Taipei, "2026-10-23T10:00+08:00"));
    }

    [Fact]
    public void Monthly_CrossesTheYear()
    {
        Assert.Equal(T("2027-01-15T09:00+08:00"), Next(Monthly("09:00", 15), Taipei, "2026-12-20T10:00+08:00"));
    }

    [Fact]
    public void Monthly_DayOfMonth31_InShortMonths_UsesTheLastDay()
    {
        var trigger = Monthly("09:00", 31);

        Assert.Equal(T("2026-02-28T09:00+08:00"), Next(trigger, Taipei, "2026-02-01T10:00+08:00"));
        Assert.Equal(T("2026-03-31T09:00+08:00"), Next(trigger, Taipei, "2026-02-28T10:00+08:00"));
        Assert.Equal(T("2026-04-30T09:00+08:00"), Next(trigger, Taipei, "2026-03-31T10:00+08:00"));
        Assert.Equal(T("2026-05-31T09:00+08:00"), Next(trigger, Taipei, "2026-04-30T10:00+08:00"));
    }

    [Fact]
    public void Monthly_DayOfMonth29_InNonLeapFebruary_UsesTwentyEighth()
    {
        Assert.Equal(T("2027-02-28T09:00+08:00"), Next(Monthly("09:00", 29), Taipei, "2027-01-30T10:00+08:00"));
    }

    [Fact]
    public void Monthly_DayOfMonth29_InLeapFebruary_IsTheTwentyNinth()
    {
        // 2028 是閏年
        Assert.Equal(T("2028-02-29T09:00+08:00"), Next(Monthly("09:00", 29), Taipei, "2028-01-30T10:00+08:00"));
    }

    [Fact]
    public void Monthly_DayOfMonth30_InFebruary_UsesTheLastDay()
    {
        Assert.Equal(T("2026-02-28T09:00+08:00"), Next(Monthly("09:00", 30), Taipei, "2026-02-01T10:00+08:00"));
    }

    [Fact]
    public void Monthly_DayOfMonthOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Next(Monthly("09:00", 0), Taipei, "2026-10-23T10:00+08:00"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Next(Monthly("09:00", 32), Taipei, "2026-10-23T10:00+08:00"));
    }

    [Fact]
    public void Taipei_LocalNineAm_IsOneAmUtc()
    {
        var next = Next(Daily("09:00"), Taipei, "2026-10-23T00:00Z");

        Assert.Equal(T("2026-10-23T01:00Z"), next);
    }

    [Fact]
    public void Utc_LocalNineAm_IsNineAmUtc()
    {
        Assert.Equal(T("2026-10-23T09:00Z"), Next(Daily("09:00"), "UTC", "2026-10-23T00:00Z"));
    }

    [Fact]
    public void NewYork_SpringForward_DayAfterTheTransition_IsEdt()
    {
        // 2026-03-08 美東進入夏令時間（EDT，-4）；3/7 的 09:00（EST）已過，所以下一次是 3/8 09:00 EDT
        Assert.Equal(T("2026-03-08T13:00Z"), Next(Daily("09:00"), NewYork, "2026-03-07T15:00Z"));
    }

    [Fact]
    public void NewYork_BeforeSpringForward_IsEst()
    {
        Assert.Equal(T("2026-03-07T14:00Z"), Next(Daily("09:00"), NewYork, "2026-03-07T00:00Z"));
    }

    [Fact]
    public void NewYork_SpringForward_NonexistentTime_IsPushedToTheEndOfTheGap()
    {
        // 2026-03-08 02:00 → 03:00 不存在；02:30 順延到 03:00 EDT（07:00Z）
        Assert.Equal(T("2026-03-08T07:00Z"), Next(Daily("02:30"), NewYork, "2026-03-07T12:00Z"));
    }

    [Fact]
    public void NewYork_SpringForward_TheGapStartItself_IsPushedToTheEnd()
    {
        // 02:00 正好是空窗的開頭，也要順延到 03:00
        Assert.Equal(T("2026-03-08T07:00Z"), Next(Daily("02:00"), NewYork, "2026-03-07T12:00Z"));
    }

    [Fact]
    public void NewYork_SpringForward_TheDayAfterTheGap_IsNormalAgain()
    {
        // 2026-03-09 02:30 已經是 EDT，不在空窗裡
        Assert.Equal(T("2026-03-09T06:30Z"), Next(Daily("02:30"), NewYork, "2026-03-08T12:00Z"));
    }

    [Fact]
    public void NewYork_FallBack_AmbiguousTime_TakesTheEarlierOccurrence()
    {
        // 2026-11-01 01:30 出現兩次；取較早的（仍是 EDT，-4 → 05:30Z）
        Assert.Equal(T("2026-11-01T05:30Z"), Next(Daily("01:30"), NewYork, "2026-10-31T12:00Z"));
    }

    [Fact]
    public void NewYork_FallBack_AfterTheEarlierOccurrence_DoesNotFireTwiceOnTheSameDay()
    {
        // 第二次的 01:30（EST，06:30Z）不再取用：每天只觸發一次，下一次是隔天
        Assert.Equal(T("2026-11-02T06:30Z"), Next(Daily("01:30"), NewYork, "2026-11-01T05:30Z"));
    }

    [Fact]
    public void NewYork_FallBack_DayAfter_IsEst()
    {
        // 11/1 的 09:00 已過（15:00Z），下一次是 11/2 09:00 EST（14:00Z）
        Assert.Equal(T("2026-11-02T14:00Z"), Next(Daily("09:00"), NewYork, "2026-11-01T15:00Z"));
    }

    [Fact]
    public void London_SpringForward_NonexistentTime_IsPushedToTheEndOfTheGap()
    {
        // 2026-03-29 01:00 → 02:00 不存在；01:30 順延到 02:00 BST（01:00Z）
        Assert.Equal(T("2026-03-29T01:00Z"), Next(Daily("01:30"), London, "2026-03-28T12:00Z"));
    }

    [Fact]
    public void London_SummerTime_IsOneHourAheadOfUtc()
    {
        Assert.Equal(T("2026-03-29T08:00Z"), Next(Daily("09:00"), London, "2026-03-28T12:00Z"));
    }

    [Fact]
    public void London_FallBack_AmbiguousTime_TakesTheEarlierOccurrence()
    {
        // 2026-10-25 01:30 重複；較早的是 BST（+1，00:30Z）
        Assert.Equal(T("2026-10-25T00:30Z"), Next(Daily("01:30"), London, "2026-10-24T12:00Z"));
    }

    [Fact]
    public void UnknownTimeZone_FallsBackToLocal()
    {
        var local = TimeZoneInfo.Local.Id;

        Assert.Equal(Next(Daily("09:00"), local, "2026-10-23T00:00Z"), Next(Daily("09:00"), "Not/A_Zone", "2026-10-23T00:00Z"));
        Assert.Equal(Next(Daily("09:00"), local, "2026-10-23T00:00Z"), Next(Daily("09:00"), "", "2026-10-23T00:00Z"));
        Assert.Equal(TimeZoneInfo.Local, ScheduleCalculator.ResolveTimeZone("Not/A_Zone"));
    }

    [Fact]
    public void ResolveTimeZone_KnownIanaId_ReturnsThatZone() =>
        Assert.Equal(Taipei, ScheduleCalculator.ResolveTimeZone(Taipei).Id);

    [Fact]
    public void NextOccurrences_ReturnsTheNextCountInOrder()
    {
        var list = ScheduleCalculator.NextOccurrences(Daily("09:00"), Taipei, T("2026-10-23T10:00+08:00"), 3);

        Assert.Equal(
            new[] { T("2026-10-24T09:00+08:00"), T("2026-10-25T09:00+08:00"), T("2026-10-26T09:00+08:00") },
            list);
    }

    [Fact]
    public void NextOccurrences_OneTime_ReturnsAtMostOne()
    {
        var list = ScheduleCalculator.NextOccurrences(new OneTimeTrigger(T("2026-10-25T09:00+08:00")), Taipei, T("2026-10-23T10:00+08:00"), 3);

        Assert.Equal(new[] { T("2026-10-25T09:00+08:00") }, list);
    }

    [Fact]
    public void NextOccurrences_ZeroCount_ReturnsEmpty() =>
        Assert.Empty(ScheduleCalculator.NextOccurrences(Daily("09:00"), Taipei, T("2026-10-23T10:00+08:00"), 0));

    [Fact]
    public void NextOccurrences_StopsAtTheSearchLimit()
    {
        // 每月 1 日，從 2026-10-23 起算 730 天內約 24 次；要求 100 次只會回範圍內的
        var after = T("2026-10-23T10:00+08:00");
        var list = ScheduleCalculator.NextOccurrences(Monthly("09:00", 1), Taipei, after, 100);

        Assert.InRange(list.Count, 23, 25);
        Assert.All(list, next => Assert.True(next <= after.AddDays(ScheduleCalculator.SearchLimitDays)));
    }

    [Fact]
    public void NextOccurrence_EveryRecurrenceKind_FindsAFireTimeWithinTheSearchLimit()
    {
        var after = "2026-10-23T10:00+08:00";

        Assert.NotNull(Next(Daily("09:00"), Taipei, after));
        Assert.NotNull(Next(Weekdays("09:00"), Taipei, after));
        Assert.NotNull(Next(Weekly("09:00", DayOfWeek.Tuesday), Taipei, after));
        Assert.NotNull(Next(Monthly("09:00", 31), Taipei, after));
    }

    [Fact]
    public void Describe_Daily() =>
        Assert.Equal("每天 09:00", ScheduleCalculator.Describe(Daily("09:00"), Taipei));

    [Fact]
    public void Describe_Weekdays() =>
        Assert.Equal("每平日 09:00", ScheduleCalculator.Describe(Weekdays("09:00"), Taipei));

    [Fact]
    public void Describe_Weekly_ListsDaysMondayFirst()
    {
        // 故意給週五在前，描述要照週一到週日排
        Assert.Equal("每週一、五 09:00", ScheduleCalculator.Describe(Weekly("09:00", DayOfWeek.Friday, DayOfWeek.Monday), Taipei));
    }

    [Fact]
    public void Describe_Weekly_SingleSunday() =>
        Assert.Equal("每週日 18:30", ScheduleCalculator.Describe(Weekly("18:30", DayOfWeek.Sunday), Taipei));

    [Fact]
    public void Describe_Weekly_NullDays() =>
        Assert.Equal("每週 09:00", ScheduleCalculator.Describe(new RecurrenceTrigger(RecurrenceKind.Weekly, new TimeOnly(9, 0), null, null), Taipei));

    [Fact]
    public void Describe_Monthly_WithDay() =>
        Assert.Equal("每月 15 日 09:00", ScheduleCalculator.Describe(Monthly("09:00", 15), Taipei));

    [Fact]
    public void Describe_Monthly_NullDayIsFirst() =>
        Assert.Equal("每月 1 日 09:00", ScheduleCalculator.Describe(Monthly("09:00", null), Taipei));

    [Fact]
    public void Describe_OneTime_ShowsLocalTimeOfTheZone()
    {
        var at = T("2026-10-25T01:00Z");

        Assert.Equal("2026-10-25 09:00", ScheduleCalculator.Describe(new OneTimeTrigger(at), Taipei));
        Assert.Equal("2026-10-25 01:00", ScheduleCalculator.Describe(new OneTimeTrigger(at), "UTC"));
    }
}
