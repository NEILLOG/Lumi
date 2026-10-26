using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>工具列的用量按鈕與面板（ui-usage-and-topbar.md 1.2、2.1、3、3.1）：名稱、Tooltip、鍵盤、焦點、快滿提醒。</summary>
public class UsageFlyoutTests
{
    private sealed class Rig
    {
        public FakeModelCatalog Catalog { get; } = new();
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public ChatToolbar Toolbar { get; }

        public Rig(SequenceChatProvider? provider = null)
        {
            provider ??= new SequenceChatProvider(SequenceChatProvider.Answer("好"));
            var store = new InMemoryConversationStore();
            var service = new ConversationService(provider, store, Catalog);
            Vm = new WorkbenchViewModel(service, store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance, tester: new FakeConnectionTester());
            Window = new WorkbenchWindow(Vm);
            Window.Show();
            Window.UpdateLayout();
            Toolbar = Window.FindByName<ChatToolbar>("Toolbar");
        }

        public Button Button => Toolbar.UsageButton;

        public FlyoutBase Flyout => Button.Flyout!;

        public UsageFlyout Panel => (UsageFlyout)((Flyout)Button.Flyout!).Content!;

        public void Open()
        {
            Button.Focus();
            Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Window.UpdateLayout();
        }
    }

    [AvaloniaFact]
    public void TheUsageButton_IsA32SquareIconButton_WithTheRingTooltipAndName()
    {
        var rig = new Rig();
        var button = rig.Button;
        rig.Window.UpdateLayout();

        Assert.Equal(32, button.Bounds.Width);
        Assert.Equal(32, button.Bounds.Height);
        Assert.Contains("tertiary", button.Classes);
        Assert.Equal(rig.Vm.Usage.RingTooltip, ToolTip.GetTip(button));
        Assert.Equal(rig.Vm.Usage.RingAutomationName, AutomationProperties.GetName(button));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
        var ring = button.GetVisualDescendants().OfType<UsageRing>().Single();
        Assert.Equal(20, ring.Diameter);
        Assert.Equal(2.5, ring.Thickness);
        Assert.IsType<Flyout>(button.Flyout);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheUsageButton_SitsBetweenTheCapsuleAndTheMoreButton()
    {
        var rig = new Rig();
        rig.Window.UpdateLayout();

        var capsuleRight = ((Visual)rig.Toolbar.CapsuleButton).TranslatePoint(new Point(rig.Toolbar.CapsuleButton.Bounds.Width, 0), rig.Toolbar)!.Value.X;
        var usageLeft = ((Visual)rig.Button).TranslatePoint(default, rig.Toolbar)!.Value.X;
        var usageRight = usageLeft + rig.Button.Bounds.Width;
        var moreLeft = ((Visual)rig.Toolbar.MoreButton).TranslatePoint(default, rig.Toolbar)!.Value.X;
        Assert.True(capsuleRight <= usageLeft + 0.5);
        Assert.True(usageRight <= moreLeft + 0.5);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Enter_OpensThePanel_AndEsc_ClosesIt_AndFocusReturnsToTheButton()
    {
        var rig = new Rig();

        rig.Open();
        Assert.True(rig.Flyout.IsOpen);
        Assert.Contains("open", rig.Button.Classes);

        // 面板的 Esc（隧道路由）：關閉並把焦點還給按鈕
        rig.Panel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = rig.Panel });
        rig.Window.UpdateLayout();

        Assert.False(rig.Flyout.IsOpen);
        Assert.DoesNotContain("open", rig.Button.Classes);
        Assert.True(rig.Button.IsFocused);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Space_AlsoOpensThePanel()
    {
        var rig = new Rig();
        rig.Button.Focus();

        rig.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        rig.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        rig.Window.UpdateLayout();

        Assert.True(rig.Flyout.IsOpen);
        rig.Flyout.Hide();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheDangerState_ShowsTheRedDot_TheDangerBackground_AndTheNearLimitReminder()
    {
        var rig = new Rig();
        rig.Catalog.Current = rig.Catalog.Current with { Models = [FakeModelCatalog.Model with { ContextWindow = 30 }] };
        rig.Catalog.Raise();
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        rig.Window.UpdateLayout();
        // 回應已完成：用量是 20 + 8 = 28 of 30（93%）
        Assert.Equal(UsageLevel.Danger, rig.Vm.Usage.Level);

        Assert.Contains("danger", rig.Button.Classes);
        var dot = rig.Button.GetVisualDescendants().OfType<Ellipse>().Single();
        Assert.True(dot.IsEffectivelyVisible);
        Assert.Equal(10, dot.Width);

        rig.Open();
        var reminder = rig.Panel.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.Usage_NearLimitNote);
        Assert.True(reminder.IsEffectivelyVisible);
        var newConversation = rig.Panel.FindByName<Button>("NewConversationInFlyout");
        Assert.Equal(Strings.Usage_NewConversation, newConversation.Content);
        Assert.True(newConversation.IsEffectivelyEnabled);
        Assert.Equal(Strings.Usage_KeepGoing, rig.Panel.FindByName<Button>("KeepGoingInFlyout").Content);
        rig.Flyout.Hide();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void BelowDanger_NoDotAndNoReminder()
    {
        var rig = new Rig();
        rig.Window.UpdateLayout();

        Assert.DoesNotContain("danger", rig.Button.Classes);
        var dot = rig.Button.GetVisualDescendants().OfType<Ellipse>().Single();
        Assert.False(dot.IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileResponding_TheButtonStaysUsable_ButNewConversationInTheReminderIsDisabled()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 };
        var rig = new Rig(provider);
        await rig.Vm.InitializeAsync();
        rig.Catalog.Current = rig.Catalog.Current with { Models = [FakeModelCatalog.Model with { ContextWindow = 30 }] };
        rig.Catalog.Raise();
        rig.Vm.InputText = "x";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);

        Assert.True(rig.Button.IsEffectivelyEnabled);   // 回應中用量仍可開（唯讀）
        rig.Open();
        Assert.True(rig.Flyout.IsOpen);
        Assert.False(rig.Panel.FindByName<Button>("NewConversationInFlyout")?.IsEffectivelyEnabled ?? false);
        rig.Flyout.Hide();

        await rig.Vm.StopAndWaitAsync();
        await sending;
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ThePanel_HasTheCostNote_AndHidesRetryWhenConnectionIsNotFailed()
    {
        var rig = new Rig();
        rig.Open();

        Assert.Contains(rig.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Usage_CostNote);
        Assert.DoesNotContain(rig.Panel.GetVisualDescendants().OfType<Button>(), b => b.Content as string == Strings.Usage_Retry && b.IsEffectivelyVisible);
        rig.Flyout.Hide();
        rig.Window.Close();
    }
}
