using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Models;
using Lumi.Core.Updates;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>標題列的圖釘、工作台頂端的新版提醒、小精靈視窗的置頂，以及設定頁的「啟動與更新」區。</summary>
public sealed class StartupAndTopmostViewTests
{
    /// <summary>按鈕的點擊：這裡要的是它綁定的命令（直接觸發 Click 事件不會執行命令）。</summary>
    private static void Click(Button button)
    {
        Assert.True(button.Command?.CanExecute(button.CommandParameter) ?? false, button.Name);
        button.Command!.Execute(button.CommandParameter);
    }

    private sealed class NoNativeOverlay : Lumi.Core.Platform.IOverlayPlatform
    {
        public string Name => "none";
        public bool IsSupported => false;
        public bool ClickThroughActive => false;
        public void Attach(nint nativeWindowHandle) { }
        public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out Lumi.Core.Platform.OverlayPoint dip) { dip = default; return false; }
        public void SetClickThrough(bool on) { }
    }

    private sealed class WorkbenchRig
    {
        public FakeModelCatalog Catalog { get; } = new();
        public FakeUpdateService Updates { get; } = new();
        public FakeUrlLauncher Launcher { get; } = new();
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }

        public WorkbenchRig()
        {
            var store = new InMemoryConversationStore();
            var service = new Lumi.Core.Chat.ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, Catalog);
            var notice = new UpdateNoticeViewModel(Updates, Launcher, new SyncUiDispatcher());
            Vm = new WorkbenchViewModel(service, store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance, update: notice);
            Window = new WorkbenchWindow(Vm);
            Window.Show();
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public Button Pin => Window.FindByName<CustomTitleBar>("TitleBar").PinButton;

        public UpdateBanner Banner => Window.FindByName<UpdateBanner>("UpdateBannerView");

        public T InBanner<T>(string name) where T : Control => Banner.FindByName<T>(name);
    }

    // ---- 圖釘 ----

    [AvaloniaFact]
    public void ThePinButton_IsInTheTitleBar_WithAnAccessibleNameAndTooltip()
    {
        var rig = new WorkbenchRig();

        Assert.True(rig.Pin.IsEffectivelyVisible);
        Assert.Equal(Strings.Workbench_Topmost, Avalonia.Automation.AutomationProperties.GetName(rig.Pin));
        Assert.Equal(Strings.Workbench_TopmostPin, ToolTip.GetTip(rig.Pin));
        Assert.False(rig.Window.Topmost);
        Assert.DoesNotContain("pinned", rig.Pin.Classes);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ClickingThePin_KeepsTheWindowOnTop_AndClickingAgainReleasesIt()
    {
        var rig = new WorkbenchRig();

        Click(rig.Pin);
        rig.Settle();
        Assert.True(rig.Window.Topmost);
        Assert.Contains("pinned", rig.Pin.Classes);
        Assert.Equal(Strings.Workbench_TopmostUnpin, ToolTip.GetTip(rig.Pin));
        Assert.True(rig.Catalog.Current.Behavior.WorkbenchTopmost);

        Click(rig.Pin);
        rig.Settle();
        Assert.False(rig.Window.Topmost);
        Assert.DoesNotContain("pinned", rig.Pin.Classes);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ThePin_IsHiddenWhereTheWorkbenchButtonsAreHidden()
    {
        var bar = new CustomTitleBar { ShowSpriteButton = false };
        var window = new Window { Content = bar, DataContext = null };
        window.Show();

        Assert.False(bar.PinButton.IsVisible);
        window.Close();
    }

    // ---- 新版提醒 ----

    [AvaloniaFact]
    public void WithoutANewVersion_TheBannerTakesNoSpace()
    {
        var rig = new WorkbenchRig();

        Assert.False(rig.Banner.IsEffectivelyVisible);
        Assert.Equal(0, rig.Banner.Bounds.Height);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ANewVersion_ShowsTheBannerWithItsActions_AndTheNotesStayCollapsed()
    {
        var rig = new WorkbenchRig();

        rig.Updates.Announce(FakeUpdateService.Release("9.9.9"));
        rig.Settle();

        Assert.True(rig.Banner.IsEffectivelyVisible);
        Assert.Contains("9.9.9", rig.InBanner<TextBlock>("UpdateTitle").Text);
        Assert.False(rig.InBanner<Border>("UpdateNotes").IsVisible);
        foreach (var name in new[] { "UpdateDownloadButton", "UpdateNotesButton", "UpdateSkipButton", "UpdateDismissButton" })
        {
            var button = rig.InBanner<Button>(name);
            Assert.True(button.IsEffectivelyVisible, name);
            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button) ?? button.Content as string), name);
        }
        // 提醒在訊息區的上方
        Assert.True(rig.Banner.TranslatePoint(default, rig.Window)!.Value.Y < rig.Window.FindByName<ConversationView>("Conversation").TranslatePoint(default, rig.Window)!.Value.Y);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheNotesButton_ExpandsTheReleaseNotes_AndCollapsesThemAgain()
    {
        var rig = new WorkbenchRig();
        rig.Updates.Announce(FakeUpdateService.Release("9.9.9"));
        rig.Settle();

        Click(rig.InBanner<Button>("UpdateNotesButton"));
        rig.Settle();
        Assert.True(rig.InBanner<Border>("UpdateNotes").IsEffectivelyVisible);
        Assert.Equal(Strings.Update_HideNotes, rig.InBanner<Button>("UpdateNotesButton").Content);

        Click(rig.InBanner<Button>("UpdateNotesButton"));
        rig.Settle();
        Assert.False(rig.InBanner<Border>("UpdateNotes").IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheDownloadSkipAndCloseButtons_DoTheirJob()
    {
        var rig = new WorkbenchRig();
        rig.Updates.Announce(FakeUpdateService.Release("9.9.9"));
        rig.Settle();

        Click(rig.InBanner<Button>("UpdateDownloadButton"));
        Assert.Single(rig.Launcher.Opened);

        Click(rig.InBanner<Button>("UpdateSkipButton"));
        rig.Settle();
        Assert.Equal(["9.9.9"], rig.Updates.Skipped.Select(u => u.Version));
        Assert.False(rig.Banner.IsEffectivelyVisible);

        rig.Updates.Announce(FakeUpdateService.Release("9.9.10"));
        rig.Settle();
        Click(rig.InBanner<Button>("UpdateDismissButton"));
        rig.Settle();
        Assert.Equal(1, rig.Updates.Dismissals);
        Assert.False(rig.Banner.IsEffectivelyVisible);
        rig.Window.Close();
    }

    // ---- 小精靈視窗 ----

    private static SpriteWindow NewSprite(FakeModelCatalog catalog) =>
        new(new SpriteCatalog([new OrbSpriteFactory()]), new NoNativeOverlay(), catalog, new SpriteShellOptions(), new FakeTimeProvider(),
            NullLogger<SpriteWindow>.Instance);

    [AvaloniaFact]
    public void TheSprite_IsOnTop_UnlessTheSettingSaysOtherwise()
    {
        var catalog = new FakeModelCatalog();
        Assert.True(NewSprite(catalog).Topmost);

        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(SpriteTopmost: false) };
        Assert.False(NewSprite(catalog).Topmost);
    }

    [AvaloniaFact]
    public async Task ChangingTheSetting_MovesTheRunningSpriteImmediately()
    {
        var catalog = new FakeModelCatalog();
        var sprite = NewSprite(catalog);
        sprite.Show();

        await catalog.SetBehaviorAsync(new BehaviorSettings(SpriteTopmost: false));
        Dispatcher.UIThread.RunJobs();
        Assert.False(sprite.Topmost);

        await catalog.SetBehaviorAsync(new BehaviorSettings(SpriteTopmost: true));
        Dispatcher.UIThread.RunJobs();
        Assert.True(sprite.Topmost);
        sprite.Close();
    }

    // ---- 設定頁 ----

    [AvaloniaFact]
    public void TheGeneralPage_HasTheStartupSection_WithItsControls()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(host.Settings) { Width = 1040, Height = 760 };
        window.Show();
        host.Settings.CategoryIndex = (int)SettingsCategory.General;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var launch = window.FindByName<CheckBox>("LaunchAtLoginBox");
        var check = window.FindByName<CheckBox>("CheckForUpdatesBox");
        Assert.True(launch.IsEffectivelyVisible && launch.IsEnabled);
        Assert.True(check.IsEffectivelyVisible);
        Assert.Equal(true, check.IsChecked);
        Assert.False(window.FindByName<TextBlock>("LaunchUnsupportedNote").IsVisible);
        Assert.Contains(AppVersion.Current, window.FindByName<TextBlock>("VersionText").Text);
        Assert.True(window.FindByName<Button>("CheckUpdateButton").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingTheLaunchCheckbox_TurnsOnLaunchAtLogin_AndWhenUnsupportedItExplainsWhy()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(host.Settings) { Width = 1040, Height = 760 };
        window.Show();
        host.Settings.CategoryIndex = (int)SettingsCategory.General;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        window.FindByName<CheckBox>("LaunchAtLoginBox").IsChecked = true;
        Assert.True(host.LaunchAtLogin.Enabled);
        window.Close();

        var unsupported = new SettingsHost(new AvaloniaUiDispatcher());
        var general = new GeneralViewModel(unsupported.Catalog, new AvaloniaUiDispatcher(), launchAtLogin: new FakeLaunchAtLogin(supported: false));
        Assert.True(general.Startup.LaunchUnsupported);
        unsupported.Dispose();
    }

    [AvaloniaFact]
    public async Task TheSpritePage_HasTheTopmostSwitch_OnByDefault()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(host.Settings) { Width = 1040, Height = 760 };
        window.Show();
        host.Settings.CategoryIndex = (int)SettingsCategory.Sprite;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var box = window.FindByName<CheckBox>("SpriteTopmostBox");
        Assert.True(box.IsEffectivelyVisible);
        Assert.Equal(true, box.IsChecked);

        box.IsChecked = false;
        await SettingsHost.WaitUntil(() => !host.Catalog.Current.Behavior.SpriteTopmost);
        Assert.False(host.Catalog.Current.Behavior.SpriteTopmost);
        window.Close();
    }
}
