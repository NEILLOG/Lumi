using System.Globalization;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Context;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>M24：提醒排程面板的新列與編輯區能建出來、沒有例外（啟停開關、編輯鈕、展開的權限與範圍）。</summary>
public class RemindersEditPanelTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

    private static (RemindersViewModel Vm, ReminderService Service, Window Window) Open()
    {
        var time = new ManualTimeProvider(Start);
        var service = new ReminderService(new InMemoryScheduledItemStore(), new Lumi.Core.Reminders.NullNotificationSink(), time, new ReminderOptions(), NullLogger<ReminderService>.Instance);
        var vm = new RemindersViewModel(service, new FakeConfirmService(), new SyncUiDispatcher(), time);
        var window = new Window { Width = 700, Height = 900, Content = new RemindersPanel { DataContext = vm } };
        window.Show();
        window.UpdateLayout();
        return (vm, service, window);
    }

    private static IEnumerable<T> Descendants<T>(Window window) where T : Visual => window.GetVisualDescendants().OfType<T>();

    [AvaloniaFact]
    public async Task PromptRow_Expanded_ShowsThePromptBox_TheGrantBoxes_AndTheSwitchAndEdit()
    {
        var (vm, service, window) = Open();
        await service.CreatePromptScheduleAsync(new NewPromptSchedule("晨報", "整理今天的行程",
            new OneTimeTrigger(Start.AddHours(1)), "Asia/Taipei", new ScheduleGrant(Write: true, Network: false, Destructive: false),
            [new ScheduleScopeItem(ContextKind.Folder, "/Users/me/Notes", "筆記")], ScheduledCreator.User), default);
        await vm.ReloadAsync();
        window.UpdateLayout();

        vm.Pending[0].ToggleEditCommand.Execute(null);
        window.UpdateLayout();

        var prompt = Descendants<TextBox>(window).Single(t => AutomationProperties.GetName(t) == Strings.Reminders_EditPrompt);
        Assert.Equal("整理今天的行程", prompt.Text);
        // 讀取（鎖定）、寫入、網路、破壞性
        var checks = Descendants<CheckBox>(window).ToList();
        Assert.Equal(4, checks.Count);
        Assert.False(checks[0].IsEnabled);
        Assert.True(checks[0].IsChecked);
        Assert.True(checks[1].IsChecked);
        Assert.Single(Descendants<ToggleSwitch>(window));
        Assert.True(Descendants<ToggleSwitch>(window).Single().IsEffectivelyVisible);
        Assert.Contains(Descendants<Button>(window), b => AutomationProperties.GetName(b) == Strings.Reminders_EditSave);
        Assert.Contains(Descendants<Button>(window), b => AutomationProperties.GetName(b) == string.Format(CultureInfo.CurrentCulture, Strings.Reminders_EditScopeRemoveTipFormat, "筆記"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReminderRow_HasTheSwitch_ButNoEditor()
    {
        var (vm, service, window) = Open();
        await service.CreateReminderAsync(new NewReminder("喝水", "時間到了", new OneTimeTrigger(Start.AddHours(1)), "Asia/Taipei",
            ScheduledKind.Reminder, ScheduledCreator.User), default);
        await vm.ReloadAsync();
        window.UpdateLayout();

        Assert.Single(Descendants<ToggleSwitch>(window));
        // 編輯區與編輯鈕還在樹上，但沒有展開、也不能用的一列不顯示
        Assert.DoesNotContain(Descendants<TextBox>(window), t => t.IsEffectivelyVisible);
        Assert.DoesNotContain(Descendants<Button>(window), b => AutomationProperties.GetName(b) == Strings.Reminders_Edit && b.IsEffectivelyVisible);
        window.Close();
    }
}
