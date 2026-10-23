using System.Collections.ObjectModel;
using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Reminders;

namespace Lumi.App.Tests.Services;

/// <summary>工作台通知列的 ViewModel：最新 3 則、「還有 N 則」、全部關閉。</summary>
public class NotificationStripViewModelTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

    private static NotificationItemViewModel Item(string id) =>
        new(new AppNotification(id, $"標題 {id}", $"內容 {id}", At, NotificationKind.Reminder), () => { });

    [Fact]
    public void Empty_IsHidden_AndHasNothingToSay()
    {
        var vm = new NotificationStripViewModel(new ObservableCollection<NotificationItemViewModel>(), () => { });

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Shown);
        Assert.False(vm.HasHidden);
    }

    [Fact]
    public void UpToThree_AreAllShown_WithoutAFooter()
    {
        var items = new ObservableCollection<NotificationItemViewModel>();
        var vm = new NotificationStripViewModel(items, () => { });

        items.Insert(0, Item("a"));
        items.Insert(0, Item("b"));
        items.Insert(0, Item("c"));

        Assert.True(vm.IsVisible);
        Assert.Equal(["c", "b", "a"], vm.Shown.Select(i => i.Id));
        Assert.Equal(0, vm.HiddenCount);
        Assert.False(vm.HasHidden);
    }

    [Fact]
    public void MoreThanThree_ShowsTheNewestThree_AndCountsTheRest()
    {
        var items = new ObservableCollection<NotificationItemViewModel>();
        var vm = new NotificationStripViewModel(items, () => { });
        for (var i = 1; i <= 5; i++)
            items.Insert(0, Item($"n{i}"));

        Assert.Equal(["n5", "n4", "n3"], vm.Shown.Select(i => i.Id));
        Assert.Equal(2, vm.HiddenCount);
        Assert.True(vm.HasHidden);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Notifications_MoreCount, 2), vm.HiddenText);
    }

    [Fact]
    public void Removing_RefillsFromTheRest()
    {
        var items = new ObservableCollection<NotificationItemViewModel>();
        var vm = new NotificationStripViewModel(items, () => { });
        for (var i = 1; i <= 4; i++)
            items.Insert(0, Item($"n{i}"));

        items.RemoveAt(0);

        Assert.Equal(["n3", "n2", "n1"], vm.Shown.Select(i => i.Id));
        Assert.False(vm.HasHidden);
    }

    [Fact]
    public void DismissAllCommand_CallsBack()
    {
        var called = false;
        var vm = new NotificationStripViewModel(new ObservableCollection<NotificationItemViewModel>(), () => called = true);

        vm.DismissAllCommand.Execute(null);

        Assert.True(called);
    }
}
