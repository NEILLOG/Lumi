using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>M20 20B：設定頁「提醒排程」的畫面——待觸發與已結束兩個清單、刪除鈕、空狀態文字。面板目前還沒掛進設定視窗，所以單獨放在視窗裡。</summary>
public class RemindersPanelTests
{
    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static (RemindersViewModel Vm, ReminderService Service, Window Window) Open()
    {
        var store = new InMemoryScheduledItemStore();
        var time = new ManualTimeProvider();
        var service = new ReminderService(store, new Lumi.Core.Reminders.NullNotificationSink(), time, new ReminderOptions(), NullLogger<ReminderService>.Instance);
        var vm = new RemindersViewModel(service, new FakeConfirmService(), new SyncUiDispatcher(), time);
        var window = new Window { Width = 700, Height = 600, Content = new RemindersPanel { DataContext = vm } };
        window.Show();
        window.UpdateLayout();
        return (vm, service, window);
    }

    private static NewReminder OneTime(string title) => new(title, "時間到了", new OneTimeTrigger(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.FromHours(8))),
        "Asia/Taipei", ScheduledKind.Reminder, ScheduledCreator.User);

    [AvaloniaFact]
    public void Panel_HasPendingAndFinishedLists()
    {
        var (vm, _, window) = Open();

        Assert.NotNull(Named<ItemsControl>(window, "PendingList"));
        Assert.NotNull(Named<ItemsControl>(window, "FinishedList"));
        Assert.Empty(vm.Pending);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnePending_ShowsOneRow_WithANamedDeleteButton()
    {
        var (vm, service, window) = Open();
        await service.CreateReminderAsync(OneTime("喝水"), default);
        await vm.ReloadAsync();
        window.UpdateLayout();

        var list = Named<ItemsControl>(window, "PendingList");
        Assert.Equal(1, list.ItemCount);
        var deleteButtons = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tertiary")).ToList();
        var button = Assert.Single(deleteButtons);
        Assert.Equal(Strings.Reminders_Delete, AutomationProperties.GetName(button));
        Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
        window.Close();
    }

    [AvaloniaFact]
    public void NoPending_ShowsTheEmptyText()
    {
        var (_, _, window) = Open();

        var empty = Named<TextBlock>(window, "RemindersEmptyPending");
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Equal(Strings.Reminders_EmptyPending, empty.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void NoFinished_ShowsTheEmptyText_AndDisablesClear()
    {
        var (_, _, window) = Open();

        var empty = Named<TextBlock>(window, "RemindersEmptyFinished");
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Equal(Strings.Reminders_EmptyFinished, empty.Text);
        Assert.False(Named<Button>(window, "RemindersClearFinishedButton").IsEffectivelyEnabled);
        window.Close();
    }
}
