using System.Collections.ObjectModel;
using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;

namespace Lumi.App.Tests.Sprites;

/// <summary>M24：小精靈提醒卡片的「查看」——有目標才有；小精靈回應中停用，Tooltip 寫出原因。</summary>
public class SpriteReminderViewTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

    private static (SpriteBubbleViewModel Bubble, ObservableCollection<NotificationItemViewModel> Items, FakeScheduledRunViewer Viewer) Create(bool withTarget)
    {
        var items = new ObservableCollection<NotificationItemViewModel>();
        var viewer = new FakeScheduledRunViewer();
        var note = new AppNotification("n", "排程「晨報」完成", "今天的摘要", At, NotificationKind.ScheduledRun,
            withTarget ? new OpenScheduledRun("run-3") : null);
        items.Add(new NotificationItemViewModel(note, () => items.Clear(), viewer.OpenScheduledRunAsync));
        var bubble = new SpriteBubbleViewModel(new SpriteShellOptions(), new ManualTimeProvider(At));
        bubble.BindReminders(items);
        return (bubble, items, viewer);
    }

    [Fact]
    public void Idle_WithATarget_CanView_WithTheTitleInTheTip()
    {
        var (bubble, _, _) = Create(withTarget: true);

        Assert.True(bubble.HasReminder);
        Assert.False(bubble.IsResponding);
        Assert.True(bubble.CanViewReminder);
        Assert.Contains("排程「晨報」完成", bubble.ReminderViewTip);
    }

    [Fact]
    public void WhileTheSpriteIsResponding_CannotView_AndTheTipSaysWhy()
    {
        var (bubble, _, _) = Create(withTarget: true);

        bubble.OnRunStarted();

        Assert.True(bubble.IsResponding);
        Assert.False(bubble.CanViewReminder);
        Assert.Equal(Strings.Schedule_Notify_SpriteViewDisabled, bubble.ReminderViewTip);
    }

    [Fact]
    public void Streaming_CountsAsResponding()
    {
        var (bubble, _, _) = Create(withTarget: true);

        bubble.OnRunStarted();
        bubble.OnEvent(new TextDelta("嗨"));

        Assert.True(bubble.IsResponding);
        Assert.False(bubble.CanViewReminder);
    }

    [Fact]
    public async Task View_OpensTheRun_AndClosesTheReminder()
    {
        var (bubble, items, viewer) = Create(withTarget: true);

        await bubble.Reminder!.ViewCommand.ExecuteAsync(null);

        Assert.Equal(["run-3"], viewer.Opened);
        Assert.Empty(items);
        Assert.False(bubble.HasReminder);
    }

    [Fact]
    public void WithoutATarget_ThereIsNoViewAndNothingToEnable()
    {
        var (bubble, _, _) = Create(withTarget: false);

        Assert.False(bubble.Reminder!.HasView);
        Assert.False(bubble.CanViewReminder);
    }
}
