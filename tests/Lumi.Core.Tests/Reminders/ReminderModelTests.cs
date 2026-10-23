using Lumi.Core.Reminders;

namespace Lumi.Core.Tests.Reminders;

/// <summary>列舉數值要和舊版 schedules.json 相同（M13 匯入時不翻譯）。</summary>
public sealed class ReminderModelTests
{
    [Fact]
    public void ScheduledKind_NumericValues_MatchTheOldFormat()
    {
        Assert.Equal(0, (int)ScheduledKind.Reminder);
        Assert.Equal(1, (int)ScheduledKind.Timer);
        Assert.Equal(2, (int)ScheduledKind.Schedule);
    }

    [Fact]
    public void ScheduledStatus_NumericValues_MatchTheOldFormat()
    {
        Assert.Equal(0, (int)ScheduledStatus.Pending);
        Assert.Equal(1, (int)ScheduledStatus.Running);
        Assert.Equal(2, (int)ScheduledStatus.Completed);
        Assert.Equal(3, (int)ScheduledStatus.Cancelled);
        Assert.Equal(4, (int)ScheduledStatus.Missed);
    }

    [Fact]
    public void RecurrenceKind_NumericValues_MatchTheOldFormat()
    {
        Assert.Equal(0, (int)RecurrenceKind.Daily);
        Assert.Equal(1, (int)RecurrenceKind.Weekdays);
        Assert.Equal(2, (int)RecurrenceKind.Weekly);
        Assert.Equal(3, (int)RecurrenceKind.Monthly);
    }

    [Fact]
    public void ReminderOptions_Defaults_MatchTheOldLimits()
    {
        var options = new ReminderOptions();

        Assert.Equal(200, options.MaxPendingItems);
        Assert.Equal(TimeSpan.FromMinutes(2), options.MissedGrace);
        Assert.Equal(TimeSpan.FromDays(30), options.FinishedRetention);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxWait);
        Assert.Equal(100, options.MaxTitleChars);
        Assert.Equal(500, options.MaxMessageChars);
        Assert.Equal(10, options.MinTimerSeconds);
        Assert.Equal(86_400, options.MaxTimerSeconds);
    }

    [Fact]
    public void NewId_IsEightLowercaseHexChars()
    {
        var id = ScheduledItem.NewId();

        Assert.Matches("^[0-9a-f]{8}$", id);
    }
}
