using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>UA-9：自訂標題列的排列（Windows／macOS）、小精靈按鈕、停用 Tooltip、拖曳區不覆蓋按鈕。</summary>
public class CustomTitleBarTests
{
    private sealed class StubModes : IAppModeCoordinator
    {
        public ChatMode ActiveMode => ChatMode.Workbench;
        public bool IsSpriteTemporarilyHidden => false;
        public List<string> Calls { get; } = [];
        public void Start() { }
        public bool EnterSpriteMode() { Calls.Add("enterSprite"); return true; }
        public void OpenWorkbench(string? scrollToCallId = null) { }
        public void WorkbenchWillSend() { }
        public bool WorkbenchMayClose() => true;
        public void ShowSprite() { }
        public void HideSpriteTemporarily() { }
        public void Exit() { }
    }

    private sealed class Rig
    {
        public StubModes Modes { get; } = new();
        public ChatSession Session { get; }
        public WorkbenchViewModel Vm { get; }

        public Rig(SequenceChatProvider? provider = null)
        {
            provider ??= new SequenceChatProvider(SequenceChatProvider.Answer("好"));
            var store = new InMemoryConversationStore();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(provider, store, catalog);
            Session = new ChatSession(service, store);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance, session: Session, modes: Modes);
        }
    }

    private static (WorkbenchWindow Window, CustomTitleBar Bar) Show(Rig rig, TitleBarPlatform platform)
    {
        var window = new WorkbenchWindow(rig.Vm, rig.Modes);
        window.Show();
        var bar = window.FindByName<CustomTitleBar>("TitleBar");
        bar.Platform = platform;
        window.UpdateLayout();
        return (window, bar);
    }

    /// <summary>命中測試要先有一次渲染（compositor 的場景樹），所以先推一個畫面。</summary>
    private static IInputElement? HitTest(Window window, Point point)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window.InputHitTest(point);
    }

    private static Rect BoundsIn(Visual target, Visual root) =>
        new(target.TranslatePoint(default, root)!.Value, target.Bounds.Size);

    private static Point CenterIn(Visual target, Visual root) => BoundsIn(target, root).Center;

    // ---- 純函式 ----

    [Fact]
    public void WindowsLayout_ReservesTheSystemButtonsOnTheRight_WithADivider()
    {
        var layout = TitleBarLayout.For(TitleBarPlatform.Windows);

        Assert.True(layout.ShowDivider);
        Assert.Equal(16, layout.LeftInset);
        Assert.Equal(TitleBarLayout.WindowsCaptionButtonsWidth, layout.RightInset);
        Assert.True(layout.RightInset >= 138);   // 三顆各 46
    }

    [Fact]
    public void MacLayout_LeavesRoomForTheTrafficLightsOnTheLeft_AndNoDivider()
    {
        var layout = TitleBarLayout.For(TitleBarPlatform.MacOS);

        Assert.False(layout.ShowDivider);
        Assert.Equal(80, layout.LeftInset);
        Assert.Equal(16, layout.RightInset);
    }

    [Theory]
    [InlineData(WindowState.Normal, true, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, true, WindowState.Normal)]
    [InlineData(WindowState.Normal, false, WindowState.Normal)]
    public void DoubleClick_TogglesMaximize_OnlyWhenTheWindowCanResize(WindowState current, bool canResize, WindowState expected) =>
        Assert.Equal(expected, TitleBarLayout.NextStateOnDoubleClick(current, canResize));

    // ---- 視窗 ----

    [AvaloniaFact]
    public void TheWorkbench_ExtendsIntoTheTitleBar_AndStacksTitleBarAboveTheToolbar()
    {
        var rig = new Rig();
        var window = new WorkbenchWindow(rig.Vm, rig.Modes);
        window.Show();
        window.UpdateLayout();
        var bar = window.FindByName<CustomTitleBar>("TitleBar");

        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(TitleBarLayout.Height, window.ExtendClientAreaTitleBarHeightHint);
        Assert.Equal(TitleBarLayout.Height, bar.Bounds.Height);
        Assert.Equal(0, BoundsIn(bar, window).Top);
        // 工具列（含「…」按鈕）在標題列下方，小精靈按鈕不在工具列裡
        var more = window.FindByName<ChatToolbar>("Toolbar").MoreButton;
        Assert.True(BoundsIn(more, window).Top >= bar.Bounds.Height);
        Assert.NotNull(bar.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "SpriteModeButton"));
        window.Close();
    }

    [AvaloniaFact]
    public void Windows_TheSpriteButtonSitsLeftOfTheReservedSystemButtons_WithTheGearAndDividerBetween()
    {
        var (window, bar) = Show(new Rig(), TitleBarPlatform.Windows);
        var button = bar.SpriteModeButton;
        var gear = bar.SettingsButton;
        var divider = bar.FindByName<Border>("Divider");
        var brand = bar.FindByName<Avalonia.Controls.TextBlock>("Brand");

        var buttonBounds = BoundsIn(button, window);
        var gearBounds = BoundsIn(gear, window);
        var dividerBounds = BoundsIn(divider, window);
        Assert.True(divider.IsVisible);
        Assert.Equal(window.Bounds.Width - TitleBarLayout.WindowsCaptionButtonsWidth, dividerBounds.Right);
        var pinBounds = BoundsIn(bar.PinButton, window);
        Assert.Equal(8, pinBounds.Left - buttonBounds.Right);                     // 小精靈與圖釘間距 8
        Assert.Equal(8, gearBounds.Left - pinBounds.Right);                       // 圖釘與齒輪間距 8
        Assert.True(pinBounds.Width == 32 && pinBounds.Height == 32);             // 圖釘 32×32
        Assert.InRange(dividerBounds.Left - gearBounds.Right, 8, 12);             // 齒輪與分隔線之間 8～12
        Assert.True(gearBounds.Width == 32 && gearBounds.Height == 32);           // 齒輪 32×32
        Assert.True(buttonBounds.Width >= 36 && buttonBounds.Height >= 32);      // 最小點擊範圍
        Assert.Equal(16, BoundsIn(brand, window).Left);                          // 品牌靠左
        // 為系統按鈕保留的區域沒有任何互動控制項
        var reserved = new Point(window.Bounds.Width - TitleBarLayout.WindowsCaptionButtonsWidth / 2, TitleBarLayout.Height / 2);
        Assert.False(CustomTitleBar.IsOnInteractiveControl(HitTest(window, reserved)));
        window.Close();
    }

    [AvaloniaFact]
    public void MacOS_TheBrandMakesRoomForTheTrafficLights_AndTheGearIsFarRight()
    {
        var (window, bar) = Show(new Rig(), TitleBarPlatform.MacOS);
        var button = bar.SpriteModeButton;
        var brand = bar.FindByName<Avalonia.Controls.TextBlock>("Brand");

        Assert.False(bar.FindByName<Border>("Divider").IsVisible);
        Assert.True(BoundsIn(brand, window).Left >= 80);                         // 紅綠燈佔左側 80
        Assert.Equal(window.Bounds.Width - 16, BoundsIn(bar.SettingsButton, window).Right);   // 齒輪在最右
        Assert.Equal(8, BoundsIn(bar.PinButton, window).Left - BoundsIn(button, window).Right);   // 小精靈在圖釘左邊，間距 8
        Assert.Equal(8, BoundsIn(bar.SettingsButton, window).Left - BoundsIn(bar.PinButton, window).Right);   // 圖釘在齒輪左邊，間距 8
        // 紅綠燈所在的區域沒有互動控制項
        Assert.False(CustomTitleBar.IsOnInteractiveControl(HitTest(window, new Point(40, TitleBarLayout.Height / 2))));
        window.Close();
    }

    [AvaloniaFact]
    public void BothPlatforms_ShareTheSameBarHeightAndButtonOrder()
    {
        var (window, bar) = Show(new Rig(), TitleBarPlatform.Windows);
        var brand = bar.FindByName<Avalonia.Controls.TextBlock>("Brand");
        Assert.True(BoundsIn(brand, window).Right < BoundsIn(bar.SpriteModeButton, window).Left);
        bar.Platform = TitleBarPlatform.MacOS;
        window.UpdateLayout();
        Assert.True(BoundsIn(brand, window).Right < BoundsIn(bar.SpriteModeButton, window).Left);
        Assert.Equal(TitleBarLayout.Height, bar.Bounds.Height);
        window.Close();
    }

    // ---- 小精靈按鈕 ----

    [AvaloniaFact]
    public void TheSpriteButton_ShowsTheIconAndLabel_HasAnAccessibleName_AndCanBeClicked()
    {
        var rig = new Rig();
        var (window, bar) = Show(rig, TitleBarPlatform.Windows);
        var button = bar.SpriteModeButton;

        Assert.Equal("SpriteModeButton", button.Name);
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Equal(Strings.Workbench_SpriteMode, Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.Equal(Strings.Workbench_SpriteMode, ToolTip.GetTip(button));
        Assert.Contains(button.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), t => t.Text == Strings.Workbench_SpriteMode);
        Assert.Contains(button.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>(), icon => icon.Data is not null);

        var center = CenterIn(button, window);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);

        Assert.Equal(["enterSprite"], rig.Modes.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WhileResponding_TheButtonIsDisabled_AndTheTooltipExplainsWhy()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        var (window, bar) = Show(rig, TitleBarPlatform.Windows);
        var button = bar.SpriteModeButton;
        await rig.Vm.InitializeAsync();
        Assert.True(button.IsEffectivelyEnabled);

        rig.Vm.InputText = "x";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);

        Assert.False(button.IsEffectivelyEnabled);
        Assert.Equal(Strings.Workbench_SwitchBlocked, ToolTip.GetTip(button));
        Assert.True(ToolTip.GetShowOnDisabled(button));

        var center = CenterIn(button, window);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Assert.Empty(rig.Modes.Calls);   // 停用時點了沒有作用

        await rig.Vm.StopAndWaitAsync();
        await sending;
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Equal(Strings.Workbench_SpriteMode, ToolTip.GetTip(button));
        window.Close();
    }

    // ---- 齒輪 ----

    [AvaloniaFact]
    public async Task TheGear_OpensSettings_HasAName_AndStaysEnabledWhileResponding()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        var (window, bar) = Show(rig, TitleBarPlatform.Windows);
        var gear = bar.SettingsButton;

        Assert.Equal(Strings.Workbench_Settings, ToolTip.GetTip(gear));
        Assert.Equal(Strings.Workbench_Settings, Avalonia.Automation.AutomationProperties.GetName(gear));
        Assert.Same(rig.Vm.OpenSettingsCommand, gear.Command);
        Assert.Contains(gear.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>(), icon => icon.Data is not null);

        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "x";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);
        Assert.True(gear.IsEffectivelyEnabled);   // 回應中齒輪可用（ui-usage-and-topbar.md 第 6 節）

        await rig.Vm.StopAndWaitAsync();
        await sending;
        window.Close();
    }

    // ---- 拖曳區 ----

    [AvaloniaFact]
    public void TheDragSurface_DoesNotCoverTheButton_AndEveryBlankSpotIsDraggable()
    {
        foreach (var platform in new[] { TitleBarPlatform.Windows, TitleBarPlatform.MacOS })
        {
            var (window, bar) = Show(new Rig(), platform);
            var button = bar.SpriteModeButton;
            var buttonBounds = BoundsIn(button, window);

            // 按鈕範圍內（四角內縮 2px 到中心）命中的都是按鈕本身或它的內容
            foreach (var point in new[] { buttonBounds.Center, buttonBounds.TopLeft + new Point(4, 4), buttonBounds.BottomRight - new Point(4, 4) })
            {
                var hit = HitTest(window, point);
                Assert.True(CustomTitleBar.IsOnInteractiveControl(hit), $"{platform}: {point} 應該命中按鈕，實際是 {hit?.GetType().Name ?? "null"}");
                Assert.Same(button, (hit as Visual)?.FindAncestorOfType<Button>() ?? hit);
            }

            // 品牌與按鈕之間的空白、按鈕上方與下方的空白都是拖曳面（命中標題列、不是按鈕）
            var y = TitleBarLayout.Height / 2;
            var blank = new[]
            {
                new Point((BoundsIn(bar.FindByName<Avalonia.Controls.TextBlock>("Brand"), window).Right + buttonBounds.Left) / 2, y),
                new Point(buttonBounds.Center.X, 2),
                new Point(buttonBounds.Center.X, TitleBarLayout.Height - 2),
            };
            foreach (var point in blank)
            {
                var hit = HitTest(window, point);
                Assert.NotNull(hit);
                Assert.True(bar.IsVisualAncestorOf((Visual)hit!) || ReferenceEquals(hit, bar), $"{platform}: {point} 應該落在標題列");
                Assert.False(CustomTitleBar.IsOnInteractiveControl(hit), $"{platform}: {point} 不應該是按鈕");
            }
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PressingOnBlankSpace_DoesNotRunTheSpriteCommand()
    {
        var rig = new Rig();
        var (window, bar) = Show(rig, TitleBarPlatform.Windows);
        var brand = BoundsIn(bar.FindByName<Avalonia.Controls.TextBlock>("Brand"), window);
        var blank = new Point((brand.Right + BoundsIn(bar.SpriteModeButton, window).Left) / 2, TitleBarLayout.Height / 2);

        window.MouseDown(blank, MouseButton.Left);
        window.MouseUp(blank, MouseButton.Left);

        Assert.Empty(rig.Modes.Calls);
        window.Close();
    }
}
