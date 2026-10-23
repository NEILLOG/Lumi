using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>M9 9D：工作台視窗接線——開啟時還原、最大化只記還原後的值、關閉時寫最後的狀態、不記最小化與隱藏。</summary>
public class WorkbenchWindowMemoryTests
{
    private sealed class Rig
    {
        public FakeModelCatalog Catalog { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public WorkbenchWindow Window { get; }

        public Rig(WindowSettings? saved)
        {
            Catalog.Current = Catalog.Current with { WorkbenchWindow = saved };
            var store = new InMemoryConversationStore();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, Catalog);
            var vm = new WorkbenchViewModel(service, store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(vm, null, Catalog, Time, NullLogger<WorkbenchWindow>.Instance);
        }

        public void Show()
        {
            Window.Show();
            Pump();
        }

        /// <summary>讓 UI 迴圈處理待辦的排版與視窗事件（真實程式由 UI 迴圈自然完成；無頭測試要手動跑）。</summary>
        public void Pump()
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public WindowSettings? LastSaved => Catalog.WindowSaves.Count == 0 ? null : Catalog.WindowSaves[^1];
    }

    /// <summary>無頭環境的第一個螢幕（座標與縮放由測試平台決定，不寫死）。</summary>
    private static (PixelRect Area, double Scaling) HeadlessScreen()
    {
        var probe = new Window();
        probe.Show();
        var screen = probe.Screens.All[0];
        probe.Close();
        return (screen.WorkingArea, screen.Scaling);
    }

    private static WindowSettings InsideScreen(int width = 800, int height = 600, bool maximized = false)
    {
        var (area, scaling) = HeadlessScreen();
        return new WindowSettings(area.X + 100, area.Y + 100, width, height, maximized, scaling);
    }

    [AvaloniaFact]
    public void SavedPlacement_IsRestoredWhenTheWindowOpens()
    {
        var saved = InsideScreen();
        var rig = new Rig(saved);

        rig.Show();

        Assert.Equal(WindowState.Normal, rig.Window.WindowState);
        Assert.Equal(new PixelPoint(saved.X, saved.Y), rig.Window.Position);
        Assert.Equal(saved.Width, rig.Window.Width);
        Assert.Equal(saved.Height, rig.Window.Height);
    }

    [AvaloniaFact]
    public void NoSavedValue_KeepsTheDefaultSize()
    {
        var rig = new Rig(null);

        rig.Show();

        Assert.Equal(1040, rig.Window.Width);
        Assert.Equal(760, rig.Window.Height);
    }

    [AvaloniaFact]
    public void SavedPositionOffScreen_FallsBackToTheDefault()
    {
        var rig = new Rig(new WindowSettings(99_000, 99_000, 800, 600, false, 1.0));

        rig.Show();

        Assert.Equal(1040, rig.Window.Width);
        Assert.Equal(760, rig.Window.Height);
    }

    [AvaloniaFact]
    public void SavedMaximized_OpensMaximized_AndTheRestoredWindowUsesTheNormalBounds()
    {
        var saved = InsideScreen(maximized: true);
        var rig = new Rig(saved);

        rig.Show();
        Assert.Equal(WindowState.Maximized, rig.Window.WindowState);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Catalog.WindowSaves);                          // 開啟時還原不寫檔

        // 還原成一般視窗：回到的是儲存的正常位置與大小，不是最大化的尺寸
        rig.Window.WindowState = WindowState.Normal;
        rig.Pump();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(rig.LastSaved!.Maximized);
        Assert.Equal(saved.Width, rig.LastSaved.Width);
        Assert.Equal(saved.Height, rig.LastSaved.Height);
    }

    [AvaloniaFact]
    public void Maximize_Then_Restore_WritesTheRestoredBounds_NotTheMaximizedSize()
    {
        var saved = InsideScreen();
        var rig = new Rig(saved);
        rig.Show();

        rig.Window.WindowState = WindowState.Maximized;
        rig.Pump();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.NotNull(rig.LastSaved);
        Assert.True(rig.LastSaved!.Maximized);
        Assert.Equal(saved.Width, rig.LastSaved.Width);                 // 最大化時沿用還原後的大小
        Assert.Equal(saved.Height, rig.LastSaved.Height);

        rig.Window.WindowState = WindowState.Normal;
        rig.Pump();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(rig.LastSaved!.Maximized);
        Assert.Equal(saved.Width, rig.LastSaved.Width);
        Assert.Equal(saved.Height, rig.LastSaved.Height);
    }

    [AvaloniaFact]
    public void Resize_IsWrittenAfterTheThrottle()
    {
        var rig = new Rig(InsideScreen());
        rig.Show();
        var before = rig.Catalog.WindowSaves.Count;

        rig.Window.Width = 900;
        rig.Window.Height = 650;
        rig.Pump();
        Assert.Equal(before, rig.Catalog.WindowSaves.Count);            // 還在節流時間內
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(900, rig.LastSaved?.Width);
        Assert.Equal(650, rig.LastSaved?.Height);
    }

    [AvaloniaFact]
    public void Closing_WritesTheLastStateWithoutWaitingForTheThrottle()
    {
        var rig = new Rig(InsideScreen());
        rig.Show();

        rig.Window.Width = 950;
        rig.Pump();
        rig.Window.Close();                                              // 不推進假時間

        Assert.Equal(950, rig.LastSaved?.Width);
    }

    [AvaloniaFact]
    public void Minimized_IsNotRecorded()
    {
        var rig = new Rig(InsideScreen());
        rig.Show();
        var before = rig.Catalog.WindowSaves.Count;

        rig.Window.WindowState = WindowState.Minimized;
        rig.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(before, rig.Catalog.WindowSaves.Count);
    }

    [AvaloniaFact]
    public void Hidden_IsNotRecorded_SoModeSwitchingCannotWriteAWrongPosition()
    {
        var rig = new Rig(InsideScreen());
        rig.Show();
        rig.Window.Width = 900;
        rig.Pump();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        var before = rig.Catalog.WindowSaves.Count;

        rig.Window.Hide();
        rig.Window.Width = 500;
        rig.Pump();                                          // 隱藏中的變動不記
        rig.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(before, rig.Catalog.WindowSaves.Count);
        Assert.Equal(900, rig.LastSaved?.Width);
    }
}
