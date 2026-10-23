using System.Globalization;
using Lumi.App.Services;
using Lumi.App.Tests.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Services;

/// <summary>通知的送達：清單、去重、上限、關閉，以及兩個視窗都不可見時要求顯示小精靈。</summary>
public class NotificationCenterTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

    /// <summary>不立即執行的 UI 派送：測試可以確認「加入是丟到 UI 執行緒做的」。</summary>
    private sealed class DeferredDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int Pending => _queue.Count;

        public void Post(Action action) => _queue.Enqueue(action);

        public void Flush()
        {
            while (_queue.Count > 0)
                _queue.Dequeue()();
        }
    }

    private sealed class FakeCoordinator : IAppModeCoordinator
    {
        public int ShowSpriteCalls { get; private set; }

        public ChatMode ActiveMode { get; set; } = ChatMode.Workbench;

        public bool IsSpriteTemporarilyHidden => false;

        public void Start() { }

        public bool EnterSpriteMode() => true;

        public void OpenWorkbench(string? scrollToCallId = null) { }

        public void WorkbenchWillSend() { }

        public bool WorkbenchMayClose() => false;

        public void ShowSprite() => ShowSpriteCalls++;

        public void HideSpriteTemporarily() { }

        public void Exit() { }
    }

    private sealed class Harness
    {
        public DeferredDispatcher Dispatcher { get; } = new();

        public FakeWindowHost Windows { get; } = new();

        public FakeCoordinator Modes { get; } = new();

        public NotificationCenter Center { get; }

        public Harness()
        {
            Center = new NotificationCenter(Dispatcher, () => Windows, () => Modes, NullLogger<NotificationCenter>.Instance);
        }

        public async Task ShowAndFlushAsync(string id, NotificationKind kind = NotificationKind.Reminder)
        {
            await Center.ShowAsync(Note(id, kind), CancellationToken.None);
            Dispatcher.Flush();
        }
    }

    private static AppNotification Note(string id, NotificationKind kind = NotificationKind.Reminder) =>
        new(id, $"標題 {id}", $"內容 {id}", At, kind);

    [Fact]
    public async Task ShowAsync_QueuesTheAddOntoTheUiThread()
    {
        var h = new Harness();

        await h.Center.ShowAsync(Note("a"), CancellationToken.None);

        Assert.Empty(h.Center.Items);   // 還沒派送到 UI 執行緒
        Assert.Equal(1, h.Dispatcher.Pending);
        h.Dispatcher.Flush();
        Assert.Single(h.Center.Items);
    }

    [Fact]
    public async Task NewestItemComesFirst()
    {
        var h = new Harness();

        await h.ShowAndFlushAsync("a");
        await h.ShowAndFlushAsync("b");

        Assert.Equal(["b", "a"], h.Center.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task TheSameId_IsNotAddedTwice()
    {
        var h = new Harness();

        await h.ShowAndFlushAsync("a");
        await h.ShowAndFlushAsync("a");

        Assert.Single(h.Center.Items);
    }

    [Fact]
    public async Task KeepsAtMostTwentyItems_DroppingTheOldest()
    {
        var h = new Harness();

        for (var i = 1; i <= NotificationCenter.MaxItems + 2; i++)
            await h.ShowAndFlushAsync($"n{i}");

        Assert.Equal(NotificationCenter.MaxItems, h.Center.Items.Count);
        Assert.Equal($"n{NotificationCenter.MaxItems + 2}", h.Center.Items[0].Id);
        Assert.DoesNotContain(h.Center.Items, i => i.Id == "n1");
        Assert.DoesNotContain(h.Center.Items, i => i.Id == "n2");
        Assert.Contains(h.Center.Items, i => i.Id == "n3");
    }

    [Fact]
    public async Task Dismiss_RemovesOnlyThatItem()
    {
        var h = new Harness();
        await h.ShowAndFlushAsync("a");
        await h.ShowAndFlushAsync("b");

        h.Center.Dismiss("a");

        Assert.Equal(["b"], h.Center.Items.Select(i => i.Id));
        h.Center.Dismiss("missing");   // 不存在的 Id 不會出錯
        Assert.Single(h.Center.Items);
    }

    [Fact]
    public async Task DismissAll_ClearsEverything_AndTheItemCommandDoesToo()
    {
        var h = new Harness();
        await h.ShowAndFlushAsync("a");
        await h.ShowAndFlushAsync("b");

        h.Center.Items[0].DismissCommand.Execute(null);
        Assert.Equal(["a"], h.Center.Items.Select(i => i.Id));

        h.Center.DismissAll();
        Assert.Empty(h.Center.Items);
    }

    [Fact]
    public async Task AMissedSummary_IsMarkedAsSuch()
    {
        var h = new Harness();

        await h.ShowAndFlushAsync("missed-1", NotificationKind.MissedSummary);

        Assert.True(h.Center.Items[0] is { IsMissedSummary: true });
    }

    [Fact]
    public async Task WhenNeitherWindowIsVisible_InSpriteMode_AsksForTheSprite()
    {
        var h = new Harness();
        h.Modes.ActiveMode = ChatMode.Sprite;

        await h.ShowAndFlushAsync("a");

        Assert.Equal(1, h.Modes.ShowSpriteCalls);
    }

    [Fact]
    public async Task WhenTheWorkbenchIsVisible_DoesNotAskForTheSprite()
    {
        var h = new Harness();
        h.Windows.ShowWorkbench();

        await h.ShowAndFlushAsync("a");

        Assert.Equal(0, h.Modes.ShowSpriteCalls);
    }

    [Fact]
    public async Task WhenTheSpriteIsVisible_DoesNotAskForTheSprite()
    {
        var h = new Harness();
        h.Windows.ShowSprite();

        await h.ShowAndFlushAsync("a");

        Assert.Equal(0, h.Modes.ShowSpriteCalls);
    }

    [Fact]
    public async Task ADuplicate_DoesNotAskForTheSpriteAgain()
    {
        var h = new Harness();
        h.Modes.ActiveMode = ChatMode.Sprite;
        await h.ShowAndFlushAsync("a");

        await h.ShowAndFlushAsync("a");

        Assert.Equal(1, h.Modes.ShowSpriteCalls);
    }

    [Fact]
    public async Task WhenNeitherWindowIsVisible_InWorkbenchMode_DoesNotSwitchModes()
    {
        // 啟動時（Start() 之前）兩個視窗都還沒顯示：不能因為錯過的彙總通知就把模式切成小精靈
        var h = new Harness();

        await h.ShowAndFlushAsync("missed-1", NotificationKind.MissedSummary);

        Assert.Equal(0, h.Modes.ShowSpriteCalls);
        Assert.Single(h.Center.Items);   // 通知仍然留著，視窗出現後就看得到
    }
}
