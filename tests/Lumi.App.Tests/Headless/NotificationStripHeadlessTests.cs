using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Reminders;

namespace Lumi.App.Tests.Headless;

/// <summary>工作台通知列（headless）：出現、關閉、堆疊（最新 3 則與還有 N 則）、Narrow 寬度不擠壓關閉鈕。</summary>
public class NotificationStripHeadlessTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

    private sealed class Rig
    {
        public Rig(double width = 1040, bool narrow = false)
        {
            Vm = new NotificationStripViewModel(Items, () => Items.Clear());
            Strip = new NotificationStrip { DataContext = Vm };
            Window = new Window { Width = width, Height = 600, Content = Strip };
            if (narrow)
                Window.Classes.Add("narrow");
            Window.Show();
            Window.UpdateLayout();
        }

        public ObservableCollection<NotificationItemViewModel> Items { get; } = new();

        public NotificationStripViewModel Vm { get; }

        public NotificationStrip Strip { get; }

        public Window Window { get; }

        public void Add(string id)
        {
            NotificationItemViewModel? item = null;
            item = new NotificationItemViewModel(new AppNotification(id, $"標題 {id}", $"內容 {id}", At, NotificationKind.Reminder), () => Items.Remove(item!));
            Items.Insert(0, item);
            Window.UpdateLayout();
        }

        public List<Button> CloseButtons() =>
            Strip.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("notice-close")).ToList();

        public List<Border> Cards() =>
            Strip.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("notice")).ToList();

        public Button? DismissAllButton() =>
            Strip.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == Strings.Notifications_DismissAll);
    }

    [AvaloniaFact]
    public void Empty_TakesNoSpace_AndAddingOneShowsIt()
    {
        var rig = new Rig();
        Assert.False(rig.Strip.IsVisible);

        rig.Add("a");

        Assert.True(rig.Strip.IsVisible);
        Assert.Single(rig.Cards());
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void CloseButton_RemovesThatItem()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("b");

        var buttons = rig.CloseButtons();
        Assert.Equal(2, buttons.Count);
        buttons[0].Command!.Execute(null);   // 第一張是最新的 b

        Assert.Equal(["a"], rig.Items.Select(i => i.Id));
        rig.Window.UpdateLayout();
        Assert.Single(rig.Cards());
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void MoreThanThree_ShowsTheNewestThree_WithAFooter_AndDismissAllClearsEverything()
    {
        var rig = new Rig();
        for (var i = 1; i <= 4; i++)
            rig.Add($"n{i}");

        Assert.Equal(3, rig.Cards().Count);
        Assert.True(rig.Vm.HasHidden);
        Assert.Contains(rig.Strip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == rig.Vm.HiddenText);

        var all = rig.DismissAllButton();
        Assert.NotNull(all);
        all!.Command!.Execute(null);

        Assert.Empty(rig.Items);
        Assert.False(rig.Strip.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Narrow_KeepsTheCloseButtonWholeAndInsideTheWindow()
    {
        var rig = new Rig(width: 500, narrow: true);
        rig.Add("a");

        var button = Assert.Single(rig.CloseButtons());
        Assert.True(button.Bounds.Width >= 28 - 0.5, "關閉鈕不能被擠壓");
        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), rig.Window)!.Value.X;
        Assert.True(right <= rig.Window.Bounds.Width, "關閉鈕要在視窗之內");
        rig.Window.Close();
    }
    [AvaloniaFact]
    public void Compact_ShowsTheCardsInsideTheWindow_WithTheCloseButtonVisible()
    {
        var rig = new Rig(width: 800);
        rig.Window.Classes.Add("compact");
        rig.Add("a");
        rig.Add("b");

        var buttons = rig.CloseButtons();
        Assert.Equal(2, buttons.Count);
        foreach (var button in buttons)
        {
            Assert.True(button.IsVisible);
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), rig.Window)!.Value.X;
            Assert.True(right <= rig.Window.Bounds.Width, "關閉鈕要在視窗之內");
        }
        rig.Window.Close();
    }
}
