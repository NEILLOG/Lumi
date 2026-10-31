using System.Globalization;
using Lumi.App.Services;
using Lumi.App.Tests.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Services;

/// <summary>M24：通知的「查看」——有 Target 才有；打開排程紀錄後關掉這則；沒有 Target 的通知行為不變。</summary>
public class NotificationViewTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

    private static AppNotification RunNote(string id, string runId = "run-1") =>
        new(id, "排程「晨報」完成", "今天的摘要", At, NotificationKind.ScheduledRun, new OpenScheduledRun(runId));

    private sealed class FakeModes : IAppModeCoordinator
    {
        public int OpenWorkbenchCalls { get; private set; }

        public ChatMode ActiveMode { get; set; } = ChatMode.Workbench;

        public bool IsSpriteTemporarilyHidden => false;

        public void Start() { }

        public bool EnterSpriteMode() => true;

        public void OpenWorkbench(string? scrollToCallId = null) => OpenWorkbenchCalls++;

        public void WorkbenchWillSend() { }

        public bool WorkbenchMayClose() => false;

        public void ShowSprite() { }

        public void HideSpriteTemporarily() { }

        public void Exit() { }
    }

    [Fact]
    public void ItemWithATarget_HasView_AndItsTipCarriesTheTitle()
    {
        var item = new NotificationItemViewModel(RunNote("a"), () => { });

        Assert.True(item.HasView);
        Assert.True(item.IsScheduledRun);
        Assert.Contains(item.Title, item.ViewTip);
    }

    [Fact]
    public async Task View_OpensTheRun_AndDismissesTheNotification()
    {
        var viewer = new FakeScheduledRunViewer();
        var dismissed = 0;
        var item = new NotificationItemViewModel(RunNote("a", "run-9"), () => dismissed++, viewer.OpenScheduledRunAsync);

        await item.ViewCommand.ExecuteAsync(null);

        Assert.Equal(["run-9"], viewer.Opened);
        Assert.Equal(1, dismissed);
    }

    [Fact]
    public async Task WithoutATarget_ThereIsNoView_AndViewingDoesNothing()
    {
        var viewer = new FakeScheduledRunViewer();
        var dismissed = 0;
        var item = new NotificationItemViewModel(
            new AppNotification("b", "提醒", "喝水", At, NotificationKind.Reminder), () => dismissed++, viewer.OpenScheduledRunAsync);

        Assert.False(item.HasView);
        Assert.False(item.IsScheduledRun);
        await item.ViewCommand.ExecuteAsync(null);

        Assert.Empty(viewer.Opened);
        Assert.Equal(0, dismissed);
    }

    [Fact]
    public async Task Center_View_BringsTheWorkbenchUp_OpensTheRun_AndRemovesTheItem()
    {
        var windows = new FakeWindowHost();
        var modes = new FakeModes();
        var viewer = new FakeScheduledRunViewer();
        var center = new NotificationCenter(new SyncUiDispatcher(), () => windows, () => modes, () => viewer, NullLogger<NotificationCenter>.Instance);

        await center.ShowAsync(RunNote("a", "run-7"), CancellationToken.None);

        var item = Assert.Single(center.Items);
        Assert.True(item.HasView);
        await item.ViewCommand.ExecuteAsync(null);

        Assert.Equal(1, modes.OpenWorkbenchCalls);
        Assert.Equal(["run-7"], viewer.Opened);
        Assert.Empty(center.Items);
    }
}
