using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Models;
using Lumi.Core.Platform;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>殼的視窗（headless）：用假的原生層驅動輪詢，確認點擊穿透、靠近浮現、狀態解析、尺寸與拖曳。</summary>
public class SpriteWindowTests
{
    private sealed class FakeOverlay : IOverlayPlatform
    {
        public string Name => "Fake";
        public bool IsSupported { get; set; } = true;
        public bool ClickThroughActive { get; private set; }
        public Point? Cursor { get; set; }
        public nint Attached { get; private set; }
        public List<bool> ClickThroughCalls { get; } = [];
        public bool ThrowOnAttach { get; set; }

        public void Attach(nint nativeWindowHandle)
        {
            if (ThrowOnAttach)
                throw new InvalidOperationException("原生層壞了");
            Attached = nativeWindowHandle;
        }

        public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out OverlayPoint dip)
        {
            dip = Cursor is { } c ? new OverlayPoint(c.X, c.Y) : default;
            return Cursor is not null;
        }

        public void SetClickThrough(bool on)
        {
            ClickThroughActive = on;
            ClickThroughCalls.Add(on);
        }
    }

    /// <summary>只支援 Idle 的最小角色，記錄收到的狀態與互動（殼不認識它是什麼）。</summary>
    private sealed class MinimalFactory : ISpriteFactory
    {
        public List<SpriteState> States { get; } = [];
        public List<SpriteInteraction> Interactions { get; } = [];

        public SpriteManifest Manifest { get; } = new(
            1, "test.minimal", "最小", "測試", "1.0.0", new Size(60, 60), 0.2, SpriteHitShape.Rect, new Point(0.5, 0),
            null, new HashSet<SpriteState> { SpriteState.Idle }, new Dictionary<SpriteState, SpriteState>());

        public ISpriteView Create(SpriteOptions options) => new View(this, options);

        private sealed class View(MinimalFactory owner, SpriteOptions options) : ISpriteView
        {
            public Control Visual { get; } = new Border { Width = options.Outer.Width, Height = options.Outer.Height };
            public Point BubbleAnchor => new(options.Outer.Width / 2, options.BoxRect.Y);
            public int PreferredFps => 30;
            public void SetState(SpriteState state) => owner.States.Add(state);
            public void OnInteraction(SpriteInteraction e) => owner.Interactions.Add(e);
            public void Advance(TimeSpan dt) { }
            public bool HitTest(Point p) => options.BoxRect.Contains(p);
        }
    }

    private sealed class Rig
    {
        public FakeOverlay Overlay { get; } = new();
        public FakeModelCatalog Catalog { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public SpriteWindow Window { get; }

        public Rig(ISpriteFactory? factory = null, SpriteSettings? settings = null)
        {
            if (settings is not null)
                Catalog.Current = Catalog.Current with { Sprite = settings };
            var catalog = new SpriteCatalog(factory is null ? [new OrbSpriteFactory()] : [factory]);
            Window = new SpriteWindow(catalog, Overlay, Catalog, new SpriteShellOptions(), Time, NullLogger<SpriteWindow>.Instance);
        }

        public SpriteLayout Layout => Window.Layout;

        public Point SpriteCenter => Layout.SpriteCenter;

        public Border Input => Window.FindControl<Border>("InputBar")!;

        public SpriteBubbleView Bubble => Window.FindControl<SpriteBubbleView>("Bubble")!;

        public void PollAt(Point? cursor, double advanceSeconds = 0.05)
        {
            Overlay.Cursor = cursor;
            Time.Advance(TimeSpan.FromSeconds(advanceSeconds));
            Window.Poll();
        }
    }

    private static readonly Point Far = new(2000, 2000);

    // ---- 版面 ----

    [AvaloniaFact]
    public void TheWindow_UsesTheFixedLayout_AndHostsTheSpriteVisual()
    {
        var rig = new Rig();

        Assert.Equal(rig.Layout.WindowSize.Width, rig.Window.Width);
        Assert.Equal(rig.Layout.WindowSize.Height, rig.Window.Height);
        var host = rig.Window.FindControl<Border>("SpriteHost")!;
        Assert.Same(rig.Window.View.Visual, host.Child);
        Assert.Equal(new Thickness(rig.Layout.SpriteOrigin.X, rig.Layout.SpriteOrigin.Y, 0, 0), host.Margin);
        Assert.Equal(rig.Window.View.Visual.Width, host.Width);
        Assert.Equal(SpriteState.Idle, rig.Window.AppliedState);
    }

    [AvaloniaTheory]
    [InlineData(SpriteSize.Small, 64)]
    [InlineData(SpriteSize.Medium, 80)]
    [InlineData(SpriteSize.Large, 104)]
    public void TheSavedSize_DecidesTheSpriteDiameter(SpriteSize size, double diameter)
    {
        var rig = new Rig(settings: SpriteSettings.Default with { Size = size });

        // 角色框是直徑的 1.2 倍，Visual 再加上每邊 40% 的特效外擴
        Assert.Equal(diameter * 1.2 * (1 + 2 * SpriteLimits.EffectMarginMax), rig.Window.View.Visual.Width, 3);
    }

    [AvaloniaFact]
    public async Task ChangingTheSize_RebuildsTheLayout_SavesIt_AndKeepsTheCenter()
    {
        var rig = new Rig();
        var before = rig.Window.SpriteCenterOnScreen;
        var smallWindowHeight = rig.Window.Height;

        await rig.Window.SetSizeAsync(SpriteSize.Large);

        Assert.True(rig.Window.Height > smallWindowHeight);
        Assert.Equal(before, rig.Window.SpriteCenterOnScreen);
        Assert.Equal(SpriteSize.Large, rig.Catalog.Current.Sprite.Size);
        Assert.Equal(rig.Layout.WindowSize.Height, rig.Window.Height);
    }

    [AvaloniaFact]
    public void AnUnknownSavedSprite_FallsBackToTheFirstRegistered()
    {
        var rig = new Rig(settings: SpriteSettings.Default with { SpriteId = "does.not.exist" });

        Assert.NotNull(rig.Window.View);
    }

    // ---- 點擊穿透與靠近 ----

    [AvaloniaFact]
    public void TheCursorOverTheSprite_TurnsClickThroughOff_AndAnywhereElseOn()
    {
        var rig = new Rig();

        rig.PollAt(Far);
        Assert.True(rig.Overlay.ClickThroughActive);          // 遠處：穿透

        rig.PollAt(rig.SpriteCenter);
        Assert.False(rig.Overlay.ClickThroughActive);         // 在角色上：可互動

        rig.PollAt(new Point(5, 5));
        Assert.True(rig.Overlay.ClickThroughActive);          // 視窗角落的透明區：穿透
    }

    [AvaloniaFact]
    public void TheInputBar_IsInteractiveOnlyOnceItHasAppeared()
    {
        var rig = new Rig();
        var onInputBar = new Point(rig.Layout.InputBar.Center.X, rig.Layout.InputBar.Center.Y);

        rig.PollAt(onInputBar);               // 還沒浮現（游標在輸入列的位置，但離角色不遠 → 靠近 → 浮現）
        rig.PollAt(onInputBar);
        Assert.Equal(1, rig.Input.Opacity);
        Assert.False(rig.Overlay.ClickThroughActive);   // 浮現之後，輸入列可以點
    }

    [AvaloniaFact]
    public void NearAppearsAfterTheCursorComes_AndGoesAfterTheHold()
    {
        var rig = new Rig();
        Assert.Equal(0, rig.Input.Opacity);

        rig.PollAt(rig.SpriteCenter + new Vector(40, 0));
        Assert.True(rig.Window.IsNear);
        Assert.Equal(1, rig.Input.Opacity);
        Assert.True(rig.Input.IsHitTestVisible);

        rig.PollAt(Far, advanceSeconds: 0.3);
        Assert.True(rig.Window.IsNear);               // 離開的延遲內還在

        rig.PollAt(Far, advanceSeconds: 0.5);
        Assert.False(rig.Window.IsNear);
        Assert.Equal(0, rig.Input.Opacity);
        Assert.False(rig.Input.IsHitTestVisible);
    }

    [AvaloniaFact]
    public void NearAndLook_AreForwardedToTheSprite()
    {
        var factory = new MinimalFactory();
        var rig = new Rig(factory);

        rig.PollAt(rig.SpriteCenter + new Vector(75, 0));

        var radius = new SpriteShellOptions().NearRadiusAt104 * 80 / 104;   // 靠近半徑隨光球直徑等比縮放
        Assert.Contains(factory.Interactions, i => i is SpriteInteraction.Near { IsNear: true });
        Assert.Contains(factory.Interactions, i => i is SpriteInteraction.Look look && Math.Abs(look.Direction.X - 75 / radius) < 0.02);
    }

    [AvaloniaFact]
    public void WithoutNativeSupport_ClickThroughIsNeverTouched_AndNearFallsBackToThePointer()
    {
        var rig = new Rig();
        rig.Overlay.IsSupported = false;

        rig.PollAt(rig.SpriteCenter);
        rig.PollAt(Far);

        Assert.Empty(rig.Overlay.ClickThroughCalls);
        Assert.False(rig.Window.IsNear);   // 沒有游標座標、指標也不在視窗內
    }

    // ---- 狀態 ----

    [AvaloniaFact]
    public void TheSprite_OnlyReceivesStatesItSupports()
    {
        var factory = new MinimalFactory();
        var rig = new Rig(factory);

        rig.Window.SetState(SpriteState.Tool);        // 預設退化：Tool → Thinking → Idle
        Assert.Equal(SpriteState.Tool, rig.Window.State);              // 要求的狀態保持原樣
        Assert.Equal(SpriteState.Idle, rig.Window.AppliedState);

        rig.Window.SetState(SpriteState.Streaming);
        rig.Window.SetState(SpriteState.Idle);

        Assert.All(factory.States, s => Assert.Equal(SpriteState.Idle, s));
    }

    [AvaloniaFact]
    public void ASupportedState_ReachesTheSpriteOnceWhenItDoesNotChange()
    {
        var rig = new Rig();

        rig.Window.SetState(SpriteState.Thinking);
        rig.Window.SetState(SpriteState.Thinking);

        Assert.Equal(SpriteState.Thinking, rig.Window.AppliedState);
    }

    [AvaloniaFact]
    public void Idle_FallsAsleepAfterAMinute_AndWakesWhenTheCursorComes()
    {
        var rig = new Rig();

        rig.PollAt(Far, advanceSeconds: 30);
        Assert.Equal(SpriteState.Idle, rig.Window.AppliedState);
        rig.PollAt(Far, advanceSeconds: 31);
        Assert.Equal(SpriteState.Sleep, rig.Window.AppliedState);
        Assert.Equal(SpriteState.Idle, rig.Window.State);      // 休眠是殼疊上去的，不是對話要求的

        rig.PollAt(rig.SpriteCenter);
        Assert.Equal(SpriteState.Idle, rig.Window.AppliedState);
    }

    [AvaloniaFact]
    public void ABusyState_NeverFallsAsleep()
    {
        var rig = new Rig();
        rig.Window.SetState(SpriteState.Thinking);

        rig.PollAt(Far, advanceSeconds: 300);

        Assert.Equal(SpriteState.Thinking, rig.Window.AppliedState);
    }

    [AvaloniaFact]
    public void ListeningOrTranscribing_ShowsTheInputBarEvenWithoutAnyone_Near()
    {
        var rig = new Rig();
        rig.Window.SetState(SpriteState.Listening);

        rig.PollAt(Far);

        Assert.Equal(1, rig.Input.Opacity);
    }

    // ---- 氣泡與輸入 ----

    [AvaloniaFact]
    public void TheBubble_ShowsTheTextAndHidesWhenCleared_AndAPlainBubbleNeverTakesTheMouse()
    {
        var rig = new Rig();
        var bubble = rig.Window.BubbleViewModel;

        bubble.OnRunStarted();
        bubble.OnEvent(new Lumi.Core.Chat.TextDelta("你好 😀"));
        rig.PollAt(Far);

        Assert.Equal(1, rig.Bubble.Opacity);
        var text = rig.Window.FindControl<SpriteBubbleView>("Bubble")!.FindControl<TextBlock>("AnswerText")!;
        var runs = text.Inlines!.OfType<Avalonia.Controls.Documents.Run>();
        Assert.Equal("你好 😀", string.Concat(runs.Select(r => r.Text)));   // 經過 emoji 處理：文字與 emoji 各一段
        Assert.False(rig.Window.FindControl<Grid>("BubbleArea")!.IsHitTestVisible);   // 沒有按鈕：點擊穿透

        bubble.Clear();
        rig.PollAt(Far);
        Assert.Equal(0, rig.Bubble.Opacity);
    }

    [AvaloniaFact]
    public void ABubbleWithButtons_TakesTheMouse_AndTurnsClickThroughOffOverItself()
    {
        var rig = new Rig();
        rig.Window.Show();
        var bubble = rig.Window.BubbleViewModel;
        bubble.OnRunStarted();
        bubble.OnEvent(Ev.Approval("c1"));
        rig.PollAt(Far);
        rig.Window.UpdateLayout();

        Assert.True(rig.Window.FindControl<Grid>("BubbleArea")!.IsHitTestVisible);
        var view = rig.Window.FindControl<SpriteBubbleView>("Bubble")!;
        var allow = view.FindControl<Button>("AllowButton")!;
        var onAllow = allow.TranslatePoint(new Point(allow.Bounds.Width / 2, allow.Bounds.Height / 2), rig.Window)!.Value;

        rig.PollAt(onAllow);
        Assert.False(rig.Overlay.ClickThroughActive);   // 在按鈕上：可以點
        Assert.Same(allow, ButtonAt(rig.Window, onAllow));   // 滑鼠真的點得到按鈕

        rig.PollAt(new Point(2, rig.Layout.WindowSize.Height - 2));   // 視窗角落
        Assert.True(rig.Overlay.ClickThroughActive);
        rig.Window.Close();
    }

    /// <summary>滑鼠在這一點會按到的按鈕（先渲染一幀，headless 的命中測試才是最新的）。</summary>
    private static Button? ButtonAt(Window window, Point point)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return (window.InputHitTest(point) as Visual)?.FindAncestorOfType<Button>(includeSelf: true);
    }

    [AvaloniaFact]
    public void AnErrorBubble_ItsViewInWorkbenchLinkCanBeClicked()
    {
        var rig = new Rig();
        rig.Window.Show();
        var bubble = rig.Window.BubbleViewModel;
        bubble.OnRunStarted();
        bubble.OnRunFinished(Ev.Finished(Ev.Failure(Lumi.Core.Chat.ChatFailureKind.BadRequest, "請求格式不正確。", retryable: false)));
        rig.PollAt(Far);
        rig.Window.UpdateLayout();

        var link = rig.Window.FindControl<SpriteBubbleView>("Bubble")!.FindControl<Button>("ErrorViewLink")!;
        var onLink = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), rig.Window)!.Value;
        rig.PollAt(onLink);

        Assert.False(rig.Overlay.ClickThroughActive);
        Assert.Same(link, ButtonAt(rig.Window, onLink));   // 滑鼠真的點得到連結
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheSpriteFollowsTheMapper_WithoutKnowingAboutTheConversation()
    {
        var rig = new Rig();

        rig.Window.Mapper.OnRunStarted();
        Assert.Equal(SpriteState.Thinking, rig.Window.AppliedState);

        rig.Window.Mapper.OnEvent(new Lumi.Core.Chat.TextDelta("嗨"));
        Assert.Equal(SpriteState.Streaming, rig.Window.AppliedState);
    }

    [AvaloniaFact]
    public void WhileResponding_TheSendButtonIsStop_AndEnterDoesNotSubmit()
    {
        var rig = new Rig();
        var submitted = new List<string>();
        var stops = 0;
        rig.Window.InputSubmitted += submitted.Add;
        rig.Window.StopRequested += () => stops++;
        var input = rig.Window.FindControl<TextBox>("Input")!;
        input.Text = "草稿";

        rig.Window.SetResponding(true);
        Assert.True(rig.Window.FindControl<Lumi.App.Controls.LumiIcon>("StopIcon")!.IsVisible);
        Assert.False(rig.Window.FindControl<Lumi.App.Controls.LumiIcon>("SendIcon")!.IsVisible);
        rig.Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, stops);
        Assert.Empty(submitted);
        Assert.Equal("草稿", input.Text);   // 草稿還在

        rig.Window.SetResponding(false);
        rig.Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["草稿"], submitted);
    }

    [AvaloniaFact]
    public void SubmittingTheInput_RaisesTheEventWithTrimmedText_AndClearsIt()
    {
        var rig = new Rig();
        var submitted = new List<string>();
        rig.Window.InputSubmitted += submitted.Add;
        var input = rig.Window.FindControl<TextBox>("Input")!;

        input.Text = "  明天會下雨嗎  ";
        rig.Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("", input.Text);

        input.Text = "   ";   // 只有空白：不送出
        rig.Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(["明天會下雨嗎"], submitted);
    }

    // ---- 幀率 ----

    [AvaloniaFact]
    public void TheWindow_StaysOutOfTheTaskbarAndTopmost()
    {
        var rig = new Rig();

        Assert.True(rig.Window.Topmost);
        Assert.False(rig.Window.ShowInTaskbar);
        Assert.False(rig.Window.CanResize);
        Assert.Equal(SystemDecorations.None, rig.Window.SystemDecorations);
    }

    // ---- 開啟與指標（真的走過 Show） ----

    [AvaloniaFact]
    public void Showing_AttachesTheNativeLayer_AndStartsPolling_WithoutThrowing()
    {
        var rig = new Rig();

        rig.Window.Show();
        rig.PollAt(Far);

        Assert.True(rig.Window.IsVisible);
        Assert.True(rig.Overlay.ClickThroughActive);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ANativeLayerThatThrows_FallsBackToTheNullOverlay_AndTheWindowStillOpens()
    {
        var rig = new Rig();
        rig.Overlay.ThrowOnAttach = true;

        rig.Window.Show();
        rig.PollAt(rig.SpriteCenter);   // 退化後沒有游標座標，不會再碰原生層

        Assert.True(rig.Window.IsVisible);
        Assert.Empty(rig.Overlay.ClickThroughCalls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ClickingTheSprite_RaisesSpriteClicked_ButClickingTheTransparentCornerDoesNot()
    {
        var rig = new Rig();
        rig.Window.Show();
        var clicks = 0;
        rig.Window.SpriteClicked += () => clicks++;

        rig.Window.MouseDown(new Point(rig.Layout.SpriteOrigin.X + 2, rig.Layout.SpriteOrigin.Y + 2), MouseButton.Left);   // 光暈區的角落
        rig.Window.MouseUp(new Point(rig.Layout.SpriteOrigin.X + 2, rig.Layout.SpriteOrigin.Y + 2), MouseButton.Left);
        Assert.Equal(0, clicks);

        rig.Window.MouseDown(rig.SpriteCenter, MouseButton.Left);
        rig.Window.MouseUp(rig.SpriteCenter, MouseButton.Left);
        Assert.Equal(1, clicks);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Pressing_And_Holding_PokesTheSprite_ThenReleaseEndsIt()
    {
        var factory = new MinimalFactory();
        var rig = new Rig(factory);
        rig.Window.Show();

        rig.Window.MouseDown(rig.SpriteCenter, MouseButton.Left);
        rig.PollAt(rig.SpriteCenter, advanceSeconds: 0.5);
        rig.Window.MouseUp(rig.SpriteCenter, MouseButton.Left);

        Assert.Contains(factory.Interactions, i => i is SpriteInteraction.PokeBegin);
        Assert.Contains(factory.Interactions, i => i is SpriteInteraction.PokeEnd);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Dragging_MovesTheWindow_AndSavesTheNewCenter()
    {
        var factory = new MinimalFactory();
        var rig = new Rig(factory, SpriteSettings.Default with { Placement = new SpritePlacement(600, 400) });
        rig.Window.Show();
        var start = rig.Window.Position;
        var startCenter = rig.Window.SpriteCenterOnScreen;

        rig.Window.MouseDown(rig.SpriteCenter, MouseButton.Left);
        rig.Window.MouseMove(rig.SpriteCenter + new Vector(40, 20));
        rig.Window.MouseMove(rig.SpriteCenter + new Vector(60, 30));
        rig.Window.MouseUp(rig.SpriteCenter + new Vector(60, 30), MouseButton.Left);

        Assert.Contains(factory.Interactions, i => i is SpriteInteraction.DragBegin);
        Assert.Contains(factory.Interactions, i => i is SpriteInteraction.DragEnd);
        Assert.NotEqual(start, rig.Window.Position);
        var saved = rig.Catalog.Current.Sprite.Placement;
        Assert.NotNull(saved);
        Assert.NotEqual(new SpritePlacement(startCenter.X, startCenter.Y), saved);
        rig.Window.Close();
    }
}
