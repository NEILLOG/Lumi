using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M24 24E：歷史抽屜的「對話｜排程」分頁（headless）：分頁切換與搜尋框、未讀圓點與可存取名稱、組與徽章的渲染、空狀態、
/// 「⋯」選單的項目與停用狀態、紀錄列的鍵盤。
/// </summary>
public class ScheduledRunsDrawerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public InMemoryConversationStore Conversations { get; } = new();

        public InMemoryScheduledItemStore Items { get; } = new();

        public InMemoryScheduledRunStore Runs { get; } = new();

        public ManualTimeProvider Time { get; } = new(Now, HistoryTestSupport.Zone(8));

        public RecordingScheduledRunService Service { get; } = new();

        public RecordingScheduledRunViewer Viewer { get; } = new();

        public FakeSettingsWindowService Settings { get; } = new();

        public FakeConfirmService Confirm { get; } = new();

        public ScheduledRunsViewModel Scheduled { get; }

        public HistoryViewModel History { get; }

        public HistoryDrawer Drawer { get; }

        public Window Window { get; }

        public Rig()
        {
            var reminders = new ReminderService(Items, new Lumi.Core.Reminders.NullNotificationSink(), Time, new ReminderOptions(),
                NullLogger<ReminderService>.Instance);
            Scheduled = new ScheduledRunsViewModel(Runs, reminders, Service, () => Viewer, Settings, Confirm, new SyncUiDispatcher(), Time,
                new ScheduledRunOptions());
            History = new HistoryViewModel(Conversations, new FakeHistoryHost(), Time, null, null, null, null, null, Scheduled);
            Drawer = new HistoryDrawer { DataContext = History };
            Window = new Window { Width = 260, Height = 700, Content = Drawer };
            Window.Show();
            Window.UpdateLayout();
        }

        public async Task SeedAsync()
        {
            await Items.AddAsync(new ScheduledItem("s-morning", ScheduledKind.Schedule, "晨報", "",
                new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(9, 0), null, null), "Asia/Taipei",
                new RunPromptAction("整理今天的信件", new ScheduleGrant(false, false, false), []),
                ScheduledStatus.Pending, true, Now.AddDays(1), null, ScheduledCreator.User, Now.AddDays(-7)));
            await Items.AddAsync(new ScheduledItem("s-empty", ScheduledKind.Schedule, "還沒跑過", "",
                new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(18, 0), null, null), "Asia/Taipei",
                new RunPromptAction("看看", new ScheduleGrant(false, false, false), []),
                ScheduledStatus.Pending, true, Now.AddDays(1), null, ScheduledCreator.User, Now.AddDays(-7)));
            await Runs.AddAsync(new ScheduledRun("r1", "s-morning", "晨報", "c1", Now.AddDays(-1), Now.AddDays(-1), Now.AddDays(-1),
                ScheduledRunTrigger.Scheduled, ScheduledRunStatus.Completed, null, null));
            await Runs.AddAsync(new ScheduledRun("r2", "s-morning", "晨報", "c2", Now, Now, null,
                ScheduledRunTrigger.Manual, ScheduledRunStatus.AwaitingApproval, null, null));
            await Scheduled.ReloadAsync();
        }

        public async Task Pump(Func<bool> condition)
        {
            for (var i = 0; i < 300 && !condition(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Assert.True(condition());
        }

        public IReadOnlyList<RadioButton> Tabs => Drawer.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "HistoryTabs").ToList();

        public TextBox SearchBox => Drawer.FindControl<TextBox>("SearchBox")!;

        public ScheduledRunsView RunsView => Drawer.GetVisualDescendants().OfType<ScheduledRunsView>().Single();

        public ListBox ChatList => Drawer.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("history-list"));

        public IEnumerable<ListBox> RunLists => RunsView.GetVisualDescendants().OfType<ListBox>().Where(l => l.Classes.Contains("run-list"));

        public IEnumerable<Button> RunMoreButtons => RunsView.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("run-more"));

        /// <summary>找不到回 null（讓 <see cref="Pump"/> 等版面排好再找）。</summary>
        public TextBlock? TextOf(string text) => Drawer.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == text);

        public bool Shows(string text) => TextOf(text)?.IsEffectivelyVisible == true;
    }

    [AvaloniaFact]
    public async Task Tabs_SwitchTheContent_AndTheSearchBoxIsOnlyOnTheChatsTab()
    {
        var rig = new Rig();
        await rig.Pump(() => rig.SearchBox.IsEffectivelyVisible);
        Assert.Equal(2, rig.Tabs.Count);

        rig.Tabs[1].IsChecked = true;

        await rig.Pump(() => !rig.SearchBox.IsEffectivelyVisible && rig.RunsView.IsEffectivelyVisible);
        Assert.False(rig.ChatList.IsEffectivelyVisible);
        Assert.True(rig.History.IsRunsTab);

        rig.Tabs[0].IsChecked = true;

        await rig.Pump(() => rig.SearchBox.IsEffectivelyVisible && rig.ChatList.IsEffectivelyVisible);
        Assert.False(rig.RunsView.IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task RunsTab_WithUnreadRuns_ShowsTheDot_AndTheUnreadName()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        var runsTab = rig.Tabs[1];
        await rig.Pump(() => rig.History.Runs.HasUnread);

        var dot = rig.Drawer.GetVisualDescendants().OfType<Ellipse>().Single(e => e.Width == 6 && e.Height == 6);
        await rig.Pump(() => dot.IsVisible);   // 點的可見性跟著 Runs.HasUnread
        Assert.Equal(Strings.Schedule_TabRunsUnread, AutomationProperties.GetName(runsTab));

        // 待確認（r2）也算未讀：兩筆都標已讀，圓點才消失
        await rig.Runs.MarkReadAsync("r1", Now);
        await rig.Runs.MarkReadAsync("r2", Now);
        await rig.Scheduled.ReloadAsync();
        await rig.Pump(() => !dot.IsVisible);
        Assert.Equal(Strings.Schedule_TabRuns, AutomationProperties.GetName(runsTab));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task RunsTab_RendersOneGroupPerSchedule_WithStatusBadges_AndTheRowsAreNewestFirst()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.History.IsRunsTab = true;

        await rig.Pump(() => rig.RunsView.IsEffectivelyVisible && rig.Shows("晨報"));

        // 兩個排程各一組（「還沒跑過」沒有紀錄，清單隱藏但組標題仍在）
        Assert.Equal(2, rig.RunLists.Count());
        Assert.True(rig.Shows("還沒跑過"));

        // 徽章：待確認與完成；紀錄由新到舊（r2 排在 r1 前面）
        await rig.Pump(() => rig.Shows(Strings.Schedule_Status_AwaitingApproval));
        Assert.True(rig.Shows(Strings.Schedule_Status_Completed));
        var rows = rig.RunLists.Single(l => l.IsEffectivelyVisible).Items.OfType<ScheduledRunRowViewModel>().Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "r2", "r1" }, rows);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task RunsTab_WithNoSchedules_ShowsTheEmptyText()
    {
        var rig = new Rig();
        rig.History.IsRunsTab = true;

        await rig.Pump(() => rig.Shows(Strings.Schedule_RunsEmpty));

        Assert.Empty(rig.RunLists);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task RunsTab_KeepNoteAndManageLink_AreAtTheBottom()
    {
        var rig = new Rig();
        rig.History.IsRunsTab = true;

        await rig.Pump(() => rig.Shows(rig.Scheduled.KeepNote));
        var link = rig.RunsView.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.Schedule_ManageInSettings));
        Assert.True(link.IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task GroupMenu_ForAnActiveSchedule_HasRunNowToggleAndEdit_AndNoDeleteRuns()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.History.IsRunsTab = true;
        await rig.Pump(() => rig.Shows("晨報"));

        var more = rig.RunsView.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("group-more") && (b.DataContext as ScheduledGroupViewModel)?.ScheduleId == "s-morning");
        var menu = Assert.IsType<MenuFlyout>(more.Flyout);
        menu.ShowAt(more);

        var shown = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Tag as string).ToArray();
        Assert.Equal(new[] { "run", "toggle", "edit" }, shown);
        Assert.Equal(Strings.Schedule_Disable, menu.Items.OfType<MenuItem>().Single(i => (i.Tag as string) == "toggle").Header);
        menu.Hide();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task RowMenu_DeleteIsDisabled_WhileTheRunIsStillWaiting()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.History.IsRunsTab = true;
        await rig.Pump(() => rig.RunMoreButtons.Count() == 2);

        // 待確認的紀錄（r2）：刪除停用，Tooltip 寫出原因
        var waiting = rig.RunMoreButtons.First(b => b.DataContext is ScheduledRunRowViewModel { Id: "r2" });
        var menu = Assert.IsType<MenuFlyout>(waiting.Flyout);
        menu.ShowAt(waiting);

        var delete = menu.Items.OfType<MenuItem>().Single();
        Assert.False(delete.IsEnabled);
        Assert.Equal(Strings.Schedule_DeleteRunBlocked, ToolTip.GetTip(delete));
        menu.Hide();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task RunRow_DeleteKey_AsksFirst_AndDoesNothingForAWaitingRun()
    {
        var rig = new Rig();
        await rig.SeedAsync();
        rig.History.IsRunsTab = true;
        await rig.Pump(() => rig.RunLists.Any(l => l.IsEffectivelyVisible));

        var list = rig.RunLists.First(l => l.IsEffectivelyVisible);
        var waiting = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext is ScheduledRunRowViewModel { Id: "r2" });
        waiting.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });

        Assert.Empty(rig.Confirm.Asked);
        Assert.Empty(rig.Service.DeletedRuns);
        rig.Window.Close();
    }
}
