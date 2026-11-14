using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// 對話區的輔助：FT-01 回到最新訊息、FT-02 中鍵拖曳捲動、FT-03 顯示回答的模型，以及等待回應時會動的三圓點。
/// </summary>
public class ConversationScrollAidsTests
{
    private sealed class Rig
    {
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public ConversationView View { get; }
        public ScrollViewer Scroller { get; }

        public Rig(string answer = "好")
        {
            var store = new InMemoryConversationStore();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer(answer)), store, catalog);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(Vm) { Width = 800, Height = 600 };
            Window.Show();
            Window.UpdateLayout();
            View = Window.FindByName<ConversationView>("Conversation");
            Scroller = View.FindByName<ScrollViewer>("MessageScroller");
        }

        // 300 行的提問（展開），讓訊息區比視窗高很多
        public async Task SendLongPromptAsync()
        {
            Vm.InputText = "長提問" + string.Concat(Enumerable.Range(1, 300).Select(i => $"\n第 {i} 行"));
            await Vm.SendCommand.ExecuteAsync(null);
            Vm.Messages.First(m => m.IsUser).IsUserTextExpanded = true;
            Window.UpdateLayout();
            Scroller.ScrollToEnd();
            Window.UpdateLayout();
        }

        public double DistanceToBottom => Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y;

        public Border JumpHost => View.FindByName<Border>("JumpToBottomHost");

        public void ScrollTo(double y)
        {
            Scroller.Offset = new Vector(0, y);
            Window.UpdateLayout();
        }

        public Point ScrollerCenter => Scroller.TranslatePoint(new Point(Scroller.Bounds.Width / 2, Scroller.Bounds.Height / 2), Window)!.Value;
    }

    // ---- FT-01 ----

    [AvaloniaFact]
    public async Task JumpToBottom_AppearsOnlyAfterScrollingUp_AndBringsTheLatestMessageBack()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        Assert.False(rig.JumpHost.IsVisible);

        rig.ScrollTo(0);
        Assert.True(rig.JumpHost.IsVisible);
        var button = rig.View.FindByName<Button>("JumpToBottomButton");
        Assert.Equal(Strings.Workbench_JumpToBottom, Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.Equal(Strings.Workbench_JumpToBottom, ToolTip.GetTip(button));

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        rig.Window.UpdateLayout();

        Assert.True(rig.DistanceToBottom <= 1, $"還差 {rig.DistanceToBottom}");
        Assert.False(rig.JumpHost.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task JumpToBottom_IsHidden_WhenTheConversationFitsTheWindow()
    {
        var rig = new Rig();
        rig.Vm.InputText = "短";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        rig.Window.UpdateLayout();

        rig.ScrollTo(0);

        Assert.False(rig.JumpHost.IsVisible);
        rig.Window.Close();
    }

    // ---- FT-02 ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(-8, 0)]
    [InlineData(48, 10)]
    [InlineData(-48, -10)]
    [InlineData(10_000, 80)]
    public void Step_HasADeadZone_GrowsWithDistance_AndIsCapped(double delta, double expected) =>
        Assert.Equal(expected, MiddleButtonAutoScroll.Step(delta));

    [AvaloniaFact]
    public async Task MiddleButton_DragScrolls_UntilReleased()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        rig.View.MiddleButtonScrollEnabled = true;
        rig.ScrollTo(0);
        var center = rig.ScrollerCenter;

        rig.Window.MouseDown(center, MouseButton.Middle);
        Assert.True(rig.View.AutoScroll.IsActive);
        Assert.NotNull(rig.View.Cursor);

        rig.Window.MouseMove(center + new Point(0, 108), RawInputModifiers.MiddleMouseButton);   // 超出 DeadZone 100 → 每格 25
        rig.View.AutoScroll.Tick();
        rig.View.AutoScroll.Tick();
        Assert.Equal(50, rig.Scroller.Offset.Y, 3);

        rig.Window.MouseMove(center - new Point(0, 108), RawInputModifiers.MiddleMouseButton);   // 往上
        rig.View.AutoScroll.Tick();
        Assert.Equal(25, rig.Scroller.Offset.Y, 3);

        rig.Window.MouseUp(center, MouseButton.Middle);
        Assert.False(rig.View.AutoScroll.IsActive);
        Assert.Null(rig.View.Cursor);
        rig.View.AutoScroll.Tick();
        Assert.Equal(25, rig.Scroller.Offset.Y, 3);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleButton_AnyKeyEndsScrolling()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        rig.View.MiddleButtonScrollEnabled = true;
        rig.ScrollTo(0);

        rig.Window.MouseDown(rig.ScrollerCenter, MouseButton.Middle);
        Assert.True(rig.View.AutoScroll.IsActive);
        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(rig.View.AutoScroll.IsActive);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleButton_DoesNothing_WhereItIsNotEnabled()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        rig.View.MiddleButtonScrollEnabled = false;   // macOS（以及 Windows 以外）的預設

        rig.Window.MouseDown(rig.ScrollerCenter, MouseButton.Middle);

        Assert.False(rig.View.AutoScroll.IsActive);
        rig.Window.MouseUp(rig.ScrollerCenter, MouseButton.Middle);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void MiddleButton_DoesNotStart_WhenNothingCanScroll()
    {
        var rig = new Rig();
        rig.View.MiddleButtonScrollEnabled = true;

        rig.Window.MouseDown(rig.ScrollerCenter, MouseButton.Middle);

        Assert.False(rig.View.AutoScroll.IsActive);
        rig.Window.MouseUp(rig.ScrollerCenter, MouseButton.Middle);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleButton_OnAWideCodeBlock_ScrollsItSideways()
    {
        var rig = new Rig("```\n" + string.Concat(Enumerable.Repeat("very_long_identifier_", 60)) + "\n```");
        rig.Vm.InputText = "給我程式碼";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        rig.Window.UpdateLayout();
        rig.View.MiddleButtonScrollEnabled = true;
        var code = rig.View.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(s => s != rig.Scroller && s.IsEffectivelyVisible && s.Extent.Width > s.Viewport.Width);
        var point = code.TranslatePoint(new Point(code.Bounds.Width / 2, code.Bounds.Height / 2), rig.Window)!.Value;

        rig.Window.MouseDown(point, MouseButton.Middle);
        rig.Window.MouseMove(point + new Point(48, 0), RawInputModifiers.MiddleMouseButton);   // 超出 DeadZone 40 → 每格 10
        rig.View.AutoScroll.Tick();

        Assert.Equal(10, code.Offset.X, 3);
        rig.Window.MouseUp(point, MouseButton.Middle);
        rig.Window.Close();
    }

    // ---- FT-03 ----

    [AvaloniaFact]
    public async Task ModelName_IsHiddenByDefault_AndShownWhenTheAdvancedSwitchIsOn()
    {
        var rig = new Rig();
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "嗨";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        rig.Window.UpdateLayout();
        var answer = rig.Vm.Messages.Last();
        Assert.Equal("m", answer.ModelId);

        bool ModelVisible() => rig.View.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Name == "ModelLabel" && t.IsEffectivelyVisible && t.Text == "m");

        Assert.False(rig.Vm.ShowModelName);
        Assert.False(ModelVisible());

        rig.Vm.ShowModelName = true;
        rig.Window.UpdateLayout();
        Assert.True(ModelVisible());
        rig.Window.Close();
    }

    // ---- 等待回應 ----

    [AvaloniaFact]
    public void Waiting_ShowsThreeAnimatedDots_InsteadOfAnEllipsis()
    {
        var rig = new Rig();
        rig.Vm.Messages.Add(MessageViewModel.User("嗨"));
        rig.Vm.Messages.Add(MessageViewModel.StreamingAssistant());
        rig.Window.UpdateLayout();

        var dots = rig.View.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "TypingDots" && p.IsEffectivelyVisible);
        Assert.Equal(3, dots.Children.OfType<Ellipse>().Count());
        Assert.All(dots.Children.OfType<Ellipse>(), e => Assert.IsType<Avalonia.Media.TranslateTransform>(e.RenderTransform));
        Assert.Equal(Strings.Workbench_Waiting, Avalonia.Automation.AutomationProperties.GetName(dots));
        Assert.DoesNotContain(rig.View.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "…");
        rig.Window.Close();
    }
}
