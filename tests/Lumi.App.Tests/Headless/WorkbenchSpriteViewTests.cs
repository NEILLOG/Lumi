using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Sprites.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>M27：工作台小精靈的版位、對齊、點擊穿透與開關（plan/27 第 8.5 節 27D）。</summary>
public sealed class WorkbenchSpriteViewTests
{
    /// <summary>框上半部是透明的（點不到），和素材型角色頭頂的空白一樣。</summary>
    private sealed class FakeSpriteView(SpriteOptions options, SpriteManifest manifest) : ISpriteView
    {
        public List<SpriteInteraction> Interactions { get; } = [];
        public SpriteState? State { get; private set; }
        public Control Visual { get; } = new Border { Width = options.Outer.Width, Height = options.Outer.Height, IsHitTestVisible = false };
        public void SetState(SpriteState state) => State = state;
        public void OnInteraction(SpriteInteraction e) => Interactions.Add(e);
        public void Advance(TimeSpan dt) { }
        public bool HitTest(Point p) => options.BoxRect.Contains(p) && p.Y >= options.BoxRect.Y + options.BoxRect.Height / 2;
        public Point BubbleAnchor { get; } = new(options.BoxRect.Center.X, options.BoxRect.Y + manifest.BubbleAnchor.Y * options.BoxRect.Height);
        public int PreferredFps => 8;
    }

    private sealed class FakeSpriteFactory(string id) : ISpriteFactory
    {
        public List<FakeSpriteView> Views { get; } = [];

        public SpriteManifest Manifest { get; } = new(
            SpriteManifest.CurrentSchemaVersion, id, id, "測試", "1.0.0", new Size(112, 112), 0, SpriteHitShape.Rect, new Point(0.5, 0.12), null,
            new HashSet<SpriteState>(Enum.GetValues<SpriteState>()), new Dictionary<SpriteState, SpriteState>());

        public ISpriteView Create(SpriteOptions options)
        {
            var view = new FakeSpriteView(options, Manifest);
            Views.Add(view);
            return view;
        }
    }

    private sealed class Rig
    {
        public FakeModelCatalog Catalog { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeSpriteFactory Corgi { get; } = new("corgi");
        public FakeSpriteFactory Robot { get; } = new("robot");
        public InMemoryConversationStore Store { get; } = new();
        public ChatSession Session { get; }
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public WorkbenchSpritePresenter Presenter { get; }

        public Rig(double width = 1040, double height = 760, bool enabled = true, string? modelId = null)
        {
            Catalog.Current = Catalog.Current with
            {
                Behavior = Catalog.Current.Behavior with { WorkbenchSprite = enabled },
                Sprite = Catalog.Current.Sprite with { SpriteId = "corgi" },
            };
            if (modelId is not null)
                Catalog.Current = Catalog.Current with { Models = [FakeModelCatalog.Model with { ModelId = modelId }] };
            var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("好"));
            var service = new ConversationService(provider, Store, Catalog);
            Session = new ChatSession(service, Store);
            var approvals = new ToolApprovalService();
            Vm = new WorkbenchViewModel(service, Store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), Time, NullLogger<WorkbenchViewModel>.Instance,
                approvals, session: Session);
            Presenter = new WorkbenchSpritePresenter(Session, approvals, new SpriteCatalog([Corgi, Robot]), Catalog, new SpriteShellOptions(), Time,
                NullLogger<WorkbenchSpritePresenter>.Instance);
            Window = new WorkbenchWindow(Vm, sprite: Presenter) { Width = width, Height = height };
            Window.Show();
            Window.UpdateLayout();
        }

        public WorkbenchSpriteHost Host => Presenter.Host;

        public FakeSpriteView View => (FakeSpriteView)Host.View!;

        public async Task StartAsync()
        {
            await Vm.InitializeAsync();
            Window.UpdateLayout();
        }

        /// <summary>送出第一則，再把瞬移跑完。</summary>
        public async Task SendFirstAsync()
        {
            await StartAsync();
            Vm.InputText = "你好";
            await Vm.SendCommand.ExecuteAsync(null);
            Advance(TimeSpan.FromMilliseconds(400));
        }

        public void Advance(TimeSpan by)
        {
            Time.Advance(by);
            Presenter.OnFrame(by);
            Window.UpdateLayout();
        }

        public void SetEnabled(bool enabled)
        {
            Catalog.Current = Catalog.Current with { Behavior = Catalog.Current.Behavior with { WorkbenchSprite = enabled } };
            Catalog.Raise();
            Window.UpdateLayout();
        }

        /// <summary>排版與繪製都跑完：視窗大小的變更、點擊判定（要有畫出來的場景）才會反映。</summary>
        public void Settle()
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public Rect BoundsOf(Visual visual)
        {
            var at = visual.TranslatePoint(default, Window)!.Value;
            return new Rect(at, visual.Bounds.Size);
        }

        /// <summary>角色本體上的一點（框的下半部）。</summary>
        public Point BodyPoint()
        {
            var host = BoundsOf(Host);
            return new Point(host.Center.X, host.Bottom - 4);
        }
    }

    [AvaloniaFact]
    public async Task Off_TheWorkbenchIsUnchanged()
    {
        var rig = new Rig(enabled: false);
        await rig.StartAsync();

        Assert.Equal(WorkbenchSpriteSlot.None, rig.Presenter.Slot);
        Assert.False(rig.Presenter.IsRunning);
        Assert.Empty(rig.Window.SpriteStage.Children);
        Assert.True(rig.Window.Conversation.WelcomeIcon.IsEffectivelyVisible);
        Assert.False(rig.Window.Conversation.WelcomeSpriteSlot.IsEffectivelyVisible);
        Assert.Equal(default, rig.Window.Toolbar.CapsuleButton.Margin);
        Assert.Empty(rig.Corgi.Views);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheStage_DoesNotMakeTheTopRowsTaller()
    {
        var off = new Rig(enabled: false);
        await off.SendFirstAsync();
        var on = new Rig();
        await on.SendFirstAsync();

        Assert.Equal(off.BoundsOf(off.Window.Toolbar).Bottom, on.BoundsOf(on.Window.Toolbar).Bottom);
        off.Window.Close();
        on.Window.Close();
    }

    [AvaloniaFact]
    public async Task ANewConversation_ShowsTheSpriteInTheWelcomeArea_InsteadOfTheIcon()
    {
        var rig = new Rig();
        await rig.StartAsync();

        Assert.Equal(WorkbenchSpriteSlot.Welcome, rig.Presenter.Slot);
        Assert.False(rig.Window.Conversation.WelcomeIcon.IsEffectivelyVisible);
        Assert.True(rig.Host.IsEffectivelyVisible);
        Assert.Equal(84, rig.Window.Conversation.WelcomeSpriteSlot.Bounds.Width);   // 60 檔的柯基
        Assert.Equal(84, rig.Window.Conversation.WelcomeSpriteSlot.Bounds.Height);
        Assert.True(rig.Presenter.IsRunning);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheFirstMessage_TeleportsToTheToolbar_WithoutJumping()
    {
        var rig = new Rig();
        await rig.StartAsync();

        rig.Vm.InputText = "你好";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        // 訊息已經出現，角色還在淡出
        Assert.True(rig.Presenter.Controller.Motion.IsTeleporting);
        Assert.Equal(WorkbenchSpriteSlot.Welcome, rig.Presenter.Slot);

        rig.Advance(TimeSpan.FromMilliseconds(130));
        Assert.Equal(WorkbenchSpriteSlot.Toolbar, rig.Presenter.Slot);
        Assert.Contains(rig.Host, rig.Window.SpriteStage.Children);
        Assert.Equal(0, rig.Host.OffsetY);

        rig.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(rig.Presenter.Controller.Motion.IsTeleporting);
        Assert.Equal(1, rig.Host.Opacity);
        Assert.Equal(0, rig.Host.OffsetY);   // 這次不跳
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningAnOldConversation_FromTheWelcomeArea_DoesNotTeleport()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        var old = await rig.Store.GetConversationAsync(rig.Session.ConversationId!);
        await rig.Vm.NewConversationCommand.ExecuteAsync(null);
        rig.Window.UpdateLayout();
        Assert.Equal(WorkbenchSpriteSlot.Welcome, rig.Presenter.Slot);   // 回到歡迎區也是直接換
        Assert.False(rig.Presenter.Controller.Motion.IsTeleporting);

        await rig.Vm.OpenConversationAsync(old!);
        rig.Window.UpdateLayout();

        Assert.Equal(WorkbenchSpriteSlot.Toolbar, rig.Presenter.Slot);
        Assert.False(rig.Presenter.Controller.Motion.IsTeleporting);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task OnTheToolbar_TheBoxSitsLeftOfTheModeButton_AndTheFeetOnTheDivider()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();

        var host = rig.BoundsOf(rig.Host);
        var button = rig.BoundsOf(rig.Window.TitleBar.SpriteModeButton);
        var toolbar = rig.BoundsOf(rig.Window.Toolbar);
        Assert.Equal(56, host.Width);   // 40 檔的柯基
        Assert.Equal(button.Left - 8, host.Right, 3);
        Assert.Equal(toolbar.Bottom - 1, host.Bottom, 3);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ALongModelName_GivesWayToTheSprite()
    {
        var rig = new Rig(modelId: "provider/" + new string('x', 200));
        await rig.SendFirstAsync();

        var capsule = rig.BoundsOf(rig.Window.Toolbar.CapsuleButton);
        Assert.True(capsule.Right <= rig.BoundsOf(rig.Host).Left - 8 + 0.5);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhenItDoesNotFit_TheSpriteHides_AndComesBackWhenWiderAgain()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        Assert.Equal(WorkbenchSpriteSlot.Toolbar, rig.Presenter.Slot);

        rig.Window.Width = 500;
        rig.Settle();
        Assert.Equal(WorkbenchSpriteSlot.None, rig.Presenter.Slot);
        Assert.Equal(default, rig.Window.Toolbar.CapsuleButton.Margin);
        Assert.False(rig.Presenter.IsRunning);

        rig.Window.Width = 1040;
        rig.Settle();
        Assert.Equal(WorkbenchSpriteSlot.Toolbar, rig.Presenter.Slot);
        Assert.True(rig.Presenter.IsRunning);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task OnlyTheBodyTakesTheMouse_TheRestFallsThroughToTheTitleBar()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        rig.Settle();
        var host = rig.BoundsOf(rig.Host);

        Assert.Same(rig.Host, rig.Window.InputHitTest(rig.BodyPoint()));
        var transparent = rig.Window.InputHitTest(new Point(host.Center.X, host.Top + 2));
        Assert.NotSame(rig.Host, transparent);
        Assert.True(transparent is Visual visual && (ReferenceEquals(visual, rig.Window.TitleBar.TitleBarSurface) || visual.FindAncestorOfType<CustomTitleBar>() is not null));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhenPeeking_ThePartBehindTheDividerIsCutAndCannotBeClicked()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        rig.Presenter.Controller.Motion.Sink();
        rig.Advance(TimeSpan.FromMilliseconds(350));
        Assert.True(rig.Host.OffsetY > 0);

        var toolbar = rig.BoundsOf(rig.Window.Toolbar);
        var hostLeft = rig.BoundsOf(rig.Host).Center.X;
        rig.Settle();

        Assert.NotSame(rig.Host, rig.Window.InputHitTest(new Point(hostLeft, toolbar.Bottom + 6)));   // 沉下去的身體在分隔線下面的位置
        Assert.True(rig.Window.SpriteStage.ClipToBounds);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task PressingAndMovingOnTheSprite_DoesNotDragIt_AndKeepsTheInputFocus()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        rig.Window.Composer.FocusInput();
        var focused = rig.Window.FocusManager?.GetFocusedElement();
        Assert.NotNull(focused);
        var left = rig.Window.SpriteStage.HostLeft;
        var at = rig.BodyPoint();

        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseMove(at + new Point(30, 20));
        rig.Window.MouseUp(at + new Point(30, 20), MouseButton.Left);
        rig.Window.UpdateLayout();

        Assert.Equal(left, rig.Window.SpriteStage.HostLeft);
        Assert.DoesNotContain(rig.View.Interactions, i => i is SpriteInteraction.DragBegin or SpriteInteraction.DragEnd);
        Assert.Equal(WorkbenchSpriteSlot.Toolbar, rig.Presenter.Slot);
        Assert.Same(focused, rig.Window.FocusManager?.GetFocusedElement());
        Assert.False(rig.Host.Focusable);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task LongPressing_PokesTheSprite()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        var at = rig.BodyPoint();

        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Time.Advance(new SpriteShellOptions().LongPress);
        rig.Presenter.Poll();
        rig.Window.MouseUp(at, MouseButton.Left);

        Assert.Contains(rig.View.Interactions, i => i is SpriteInteraction.PokeBegin);
        Assert.Contains(rig.View.Interactions, i => i is SpriteInteraction.PokeEnd);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheCursorComingNear_WakesThePeekingSprite()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(4));   // 完成狀態回待機
        rig.Presenter.Poll();
        rig.Presenter.Controller.Motion.Sink();
        rig.Advance(TimeSpan.FromMilliseconds(350));
        Assert.True(rig.Host.OffsetY > 0);

        rig.Window.MouseMove(rig.BodyPoint());
        rig.Presenter.Poll();
        rig.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Contains(rig.View.Interactions, i => i is SpriteInteraction.Near { IsNear: true });
        Assert.Equal(0, rig.Host.OffsetY);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task HidingTheWindow_StopsTheTimers_AndShowingResumes()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        Assert.True(rig.Presenter.IsRunning);

        rig.Window.Hide();
        Assert.False(rig.Presenter.IsRunning);

        rig.Window.Show();
        rig.Window.UpdateLayout();
        Assert.True(rig.Presenter.IsRunning);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TurningItOff_RemovesEverything_AndOnBringsItBack()
    {
        var rig = new Rig(modelId: "provider/" + new string('x', 200));
        await rig.SendFirstAsync();
        Assert.NotEqual(default, rig.Window.Toolbar.CapsuleButton.Margin);

        rig.SetEnabled(false);
        Assert.Equal(WorkbenchSpriteSlot.None, rig.Presenter.Slot);
        Assert.Empty(rig.Window.SpriteStage.Children);
        Assert.Equal(default, rig.Window.Toolbar.CapsuleButton.Margin);
        Assert.False(rig.Presenter.IsRunning);

        rig.SetEnabled(true);
        Assert.Equal(WorkbenchSpriteSlot.Toolbar, rig.Presenter.Slot);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ChangingTheCharacter_RebuildsIt_AndTheStateFollowsTheConversation()
    {
        var rig = new Rig();
        await rig.SendFirstAsync();
        Assert.Empty(rig.Robot.Views);

        rig.Catalog.Current = rig.Catalog.Current with { Sprite = rig.Catalog.Current.Sprite with { SpriteId = "robot" } };
        rig.Catalog.Raise();
        rig.Window.UpdateLayout();

        var robot = Assert.Single(rig.Robot.Views);
        Assert.Same(robot, rig.Host.View);
        Assert.NotNull(robot.State);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task VeryShortWindows_HideTheWelcomeArea_AndStopTheTimers()
    {
        var rig = new Rig(height: 560);
        await rig.StartAsync();
        rig.Window.UpdateLayout();

        Assert.Equal(WorkbenchSpriteSlot.Welcome, rig.Presenter.Slot);
        Assert.False(rig.Host.IsEffectivelyVisible);
        Assert.False(rig.Presenter.IsRunning);
        rig.Window.Close();
    }
}
