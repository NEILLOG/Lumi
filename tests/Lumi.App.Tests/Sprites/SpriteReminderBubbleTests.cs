using System.Collections.ObjectModel;
using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;

namespace Lumi.App.Tests.Sprites;

/// <summary>小精靈的提醒卡片（<see cref="SpriteBubbleViewModel"/> 上的獨立區塊）：最新一則、還有 N 則、截斷、不自動消失、與回應共存。</summary>
public class SpriteReminderBubbleTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

    private static NotificationItemViewModel Item(string id, string? message = null, ObservableCollection<NotificationItemViewModel>? from = null)
    {
        NotificationItemViewModel? item = null;
        item = new(new AppNotification(id, $"標題 {id}", message ?? $"內容 {id}", At, NotificationKind.Reminder),
            () => from?.Remove(item!));
        return item;
    }

    private static (SpriteBubbleViewModel Bubble, ManualTimeProvider Time) Create()
    {
        var time = new ManualTimeProvider(At);
        return (new SpriteBubbleViewModel(new SpriteShellOptions(), time), time);
    }

    [Fact]
    public void WithoutReminders_ThereIsNoCardAndNoTail()
    {
        var (bubble, _) = Create();

        bubble.BindReminders(new ObservableCollection<NotificationItemViewModel>());

        Assert.False(bubble.HasReminder);
        Assert.False(bubble.HasTail);
    }

    [Fact]
    public void OneReminder_ShowsTheCard_WithoutAMoreLine()
    {
        var (bubble, _) = Create();
        var items = new ObservableCollection<NotificationItemViewModel>();
        bubble.BindReminders(items);

        items.Add(Item("a"));

        Assert.True(bubble.HasReminder);
        Assert.Equal("a", bubble.Reminder!.Id);
        Assert.False(bubble.HasReminderMore);
        Assert.True(bubble.HasTail);
    }

    [Fact]
    public void Several_ShowTheNewest_AndCountTheRest_ThenTheNextAfterClose()
    {
        var (bubble, _) = Create();
        var items = new ObservableCollection<NotificationItemViewModel>();
        bubble.BindReminders(items);
        items.Insert(0, Item("a", from: items));
        items.Insert(0, Item("b", from: items));
        items.Insert(0, Item("c", from: items));

        Assert.Equal("c", bubble.Reminder!.Id);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Notifications_MoreCount, 2), bubble.ReminderMoreText);

        bubble.Reminder!.DismissCommand.Execute(null);

        Assert.Equal("b", bubble.Reminder!.Id);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Notifications_MoreCount, 1), bubble.ReminderMoreText);

        bubble.Reminder!.DismissCommand.Execute(null);
        Assert.Equal("a", bubble.Reminder!.Id);
        Assert.False(bubble.HasReminderMore);
    }

    [Fact]
    public void A_LongMessage_IsTruncatedLikeTheAnswerBubble()
    {
        var (bubble, _) = Create();
        var items = new ObservableCollection<NotificationItemViewModel>();
        bubble.BindReminders(items);
        var max = new SpriteShellOptions().BubbleMaxClusters;
        var message = new string('字', max + 50);

        items.Add(Item("long", message));

        // 與回應氣泡共用同一個截斷規則：字形叢集上限加上省略號
        Assert.Equal(SpriteBubbleViewModel.Truncate(message, max).Text, bubble.ReminderText);
        Assert.EndsWith(Strings.Sprite_Ellipsis, bubble.ReminderText);
    }

    [Fact]
    public void ARemindersCard_DoesNotExpire_LikeAnAnswer()
    {
        var (bubble, time) = Create();
        var items = new ObservableCollection<NotificationItemViewModel>();
        bubble.BindReminders(items);
        items.Add(Item("a"));

        time.Advance(TimeSpan.FromHours(1));
        bubble.Tick();
        time.Advance(TimeSpan.FromHours(1));
        bubble.Tick();

        Assert.True(bubble.HasReminder);
    }

    [Fact]
    public void ClearingTheAnswer_DoesNotRemoveTheReminder()
    {
        var (bubble, _) = Create();
        var items = new ObservableCollection<NotificationItemViewModel>();
        bubble.BindReminders(items);
        items.Add(Item("a"));

        bubble.OnRunStarted();
        bubble.Clear();

        Assert.True(bubble.HasReminder);
    }

    [Fact]
    public void ARemindersCard_CoexistsWithAStreamingAnswer()
    {
        var (bubble, _) = Create();
        var items = new ObservableCollection<NotificationItemViewModel>();
        bubble.BindReminders(items);
        items.Add(Item("a"));

        bubble.OnRunStarted();
        bubble.OnEvent(new TextDelta("回答中"));

        Assert.True(bubble.IsAnswer);
        Assert.True(bubble.HasReminder);
        Assert.Equal("回答中", bubble.Text);
    }
}
