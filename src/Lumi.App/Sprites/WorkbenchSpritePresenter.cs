using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Sprites;

/// <summary>工作台視窗裡，角色會用到的控制項。</summary>
internal sealed record WorkbenchSpriteViews(
    Window Window, WorkbenchSpriteStage Stage, Panel WelcomeSlot, ChatToolbar Toolbar, Control ModeButton, WorkbenchViewModel ViewModel);

/// <summary>
/// 工作台角色的畫面接線（M27，plan/27 第 8 節）：決定站在哪裡、建立角色、驅動幀率與輪詢、轉交滑鼠。
/// 規則在 <see cref="WorkbenchSpriteRules"/>，對話與動作在 <see cref="WorkbenchSpriteController"/>。
/// 功能關閉時不建角色、不開計時器，版面和沒有這個功能時一樣。
/// </summary>
public sealed class WorkbenchSpritePresenter : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ISpriteCatalog _catalog;
    private readonly IModelCatalog _settings;
    private readonly SpriteShellOptions _shell;
    private readonly ILogger<WorkbenchSpritePresenter> _logger;
    private readonly WorkbenchSpriteController _controller;
    private readonly SpriteInteractionTracker _tracker;
    private readonly WorkbenchSpriteHost _host = new();
    private readonly DispatcherTimer _poll = new() { Interval = PollInterval };

    private WorkbenchSpriteViews? _views;
    private FrameDriver? _frames;
    private ISpriteFactory? _factory;
    private ISpriteView? _view;
    private SpriteOptions? _options;
    private WorkbenchSpriteSlot _slot = WorkbenchSpriteSlot.None;
    private SpriteState? _applied;
    private bool _running;
    private bool _teleportNext;
    private bool _teleporting;
    private bool _refreshing;
    private double _reserve;
    private Point? _cursor;
    private Point _pressAt;

    public WorkbenchSpritePresenter(IChatSession session, IToolApprovalResponder approvals, ISpriteCatalog catalog, IModelCatalog settings,
        SpriteShellOptions shell, TimeProvider time, ILogger<WorkbenchSpritePresenter> logger)
    {
        _catalog = catalog;
        _settings = settings;
        _shell = shell;
        _logger = logger;
        _controller = new WorkbenchSpriteController(session, approvals, shell, time);
        _tracker = new SpriteInteractionTracker(shell, time);
    }

    internal WorkbenchSpriteController Controller => _controller;

    internal WorkbenchSpriteHost Host => _host;

    internal WorkbenchSpriteSlot Slot => _slot;

    internal bool IsRunning => _running;

    internal void Attach(WorkbenchSpriteViews views)
    {
        _views = views;
        _frames = new FrameDriver(views.Window, OnFrame);
        _poll.Tick += (_, _) => Poll();
        _controller.StateChanged += OnStateChanged;
        _tracker.Raised += OnTrackerEvent;

        _settings.Changed += OnSettingsChanged;
        if (_catalog is IReloadableSpriteCatalog reloadable)
            reloadable.Changed += OnSpritesReloaded;

        views.ViewModel.PropertyChanged += OnViewModelChanged;
        views.ViewModel.FirstMessageSending += OnFirstMessageSending;
        views.Window.LayoutUpdated += OnLayoutUpdated;
        views.Window.PropertyChanged += OnWindowPropertyChanged;
        views.Window.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        views.Window.PointerExited += OnWindowPointerExited;

        _host.PointerPressed += OnHostPressed;
        _host.PointerMoved += OnHostMoved;
        _host.PointerReleased += OnHostReleased;
        _host.PointerCaptureLost += OnHostCaptureLost;

        Refresh();
    }

    public void Dispose()
    {
        if (_views is not { } views)
            return;
        _views = null;
        Stop();
        _poll.Stop();
        _controller.Dispose();
        _controller.StateChanged -= OnStateChanged;
        _tracker.Raised -= OnTrackerEvent;
        _settings.Changed -= OnSettingsChanged;
        if (_catalog is IReloadableSpriteCatalog reloadable)
            reloadable.Changed -= OnSpritesReloaded;
        views.ViewModel.PropertyChanged -= OnViewModelChanged;
        views.ViewModel.FirstMessageSending -= OnFirstMessageSending;
        views.Window.LayoutUpdated -= OnLayoutUpdated;
        views.Window.PropertyChanged -= OnWindowPropertyChanged;
        views.Window.RemoveHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved);
        views.Window.PointerExited -= OnWindowPointerExited;
    }

    // ---- 版位 ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchViewModel.ShowWelcome) or nameof(WorkbenchViewModel.IsLoadingMessages)
            or nameof(WorkbenchViewModel.IsViewingRun) or nameof(WorkbenchViewModel.IsWorkbenchSpriteEnabled))
            Refresh();
    }

    private void OnFirstMessageSending() => _teleportNext = true;

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            Refresh();
        else if (e.Property == WindowBase.IsActiveProperty)
            UpdateFrameRate();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => OnUiThread(Refresh);

    private void OnSpritesReloaded(object? sender, EventArgs e) => OnUiThread(Refresh);

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    /// <summary>依現況重算一切：角色、版位、位置、該不該跑。排版完成時也會呼叫，所以沒有變化時不能做任何會觸發排版的事。</summary>
    private void Refresh()
    {
        if (_views is not { } views || _refreshing || _teleporting)
            return;
        _refreshing = true;
        try
        {
            var enabled = _settings.Current.Behavior.WorkbenchSprite && EnsureFactory();
            if (enabled)
                _controller.Attach();
            else
                _controller.Detach();

            var placement = enabled ? PlaceOnToolbar(views) : null;
            var viewModel = views.ViewModel;
            var next = WorkbenchSpriteRules.ResolveSlot(enabled, viewModel.ShowWelcome, viewModel.IsLoadingMessages, viewModel.IsViewingRun,
                placement?.Fits ?? false, _slot);

            if (next != _slot)
            {
                var teleport = _teleportNext && _slot == WorkbenchSpriteSlot.Welcome && _running;
                _teleportNext = false;
                if (teleport)
                {
                    _teleporting = true;
                    _controller.Motion.BeginTeleport(FinishTeleport);
                    UpdateFrameRate();
                    return;
                }
                Show(next);
            }

            ApplyPlacement(views, placement);
            UpdateRunning(views);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void FinishTeleport()
    {
        _teleporting = false;
        Refresh();
    }

    /// <summary>設定裡的角色；找不到就退回第一個（設定不動，角色回來之後自動換回去）。換了角色時重建。沒有任何角色回 false。</summary>
    private bool EnsureFactory()
    {
        var wanted = _catalog.Find(_settings.Current.Sprite.SpriteId) ?? _catalog.All.FirstOrDefault();
        if (ReferenceEquals(wanted, _factory))
            return wanted is not null;
        _factory = wanted;
        if (_slot != WorkbenchSpriteSlot.None)
            Show(_slot);
        return wanted is not null;
    }

    private SpriteOptions Fit(double tier)
    {
        var fit = SpriteLimits.Fit(_factory!.Manifest, tier);
        foreach (var warning in fit.Warnings)
            _logger.LogWarning("{Warning}", warning);
        return fit.Options;
    }

    private WorkbenchSpritePlacement? PlaceOnToolbar(WorkbenchSpriteViews views)
    {
        var buttonLeft = views.ModeButton.TranslatePoint(default, views.Window);
        if (buttonLeft is not { } left || views.Toolbar.CapsuleColumnIn(views.Window) is not { } column)
            return null;
        var boxWidth = SpriteLimits.Fit(_factory!.Manifest, WorkbenchSpriteRules.ToolbarTier).Options.Box.Width;
        return WorkbenchSpriteRules.PlaceOnToolbar(left.X, boxWidth, column.Left, column.Right);
    }

    /// <summary>把角色放到位置上（新的 view，因為檔位不同；圖片共用同一份）。</summary>
    private void Show(WorkbenchSpriteSlot slot)
    {
        var views = _views!;
        _slot = slot;
        _controller.Slot = slot;
        views.Stage.Children.Remove(_host);
        views.WelcomeSlot.Children.Remove(_host);
        _host.Clear();
        _view = null;
        _options = null;
        _applied = null;

        if (slot == WorkbenchSpriteSlot.None)
            return;

        var options = Fit(slot == WorkbenchSpriteSlot.Welcome ? WorkbenchSpriteRules.WelcomeTier : WorkbenchSpriteRules.ToolbarTier);
        _options = options;
        _view = _factory!.Create(options);
        _host.Show(_view, options);
        if (slot == WorkbenchSpriteSlot.Welcome)
        {
            // 版面只佔角色框：外擴（光暈）用負的外距溢出
            views.WelcomeSlot.Width = options.Box.Width;
            views.WelcomeSlot.Height = options.Box.Height;
            var overflowX = (options.Outer.Width - options.Box.Width) / 2;
            var overflowY = (options.Outer.Height - options.Box.Height) / 2;
            _host.Margin = new Thickness(-overflowX, -overflowY, -overflowX, -overflowY);
            views.WelcomeSlot.Children.Add(_host);
        }
        else
        {
            _host.Margin = default;
            views.Stage.Children.Add(_host);
            _controller.Motion.SetPeekDepth(WorkbenchSpriteRules.PeekDepth(options.Outer.Height, _view.BubbleAnchor.Y));
        }
        ApplyState();
        ApplyMotion();
    }

    private void ApplyPlacement(WorkbenchSpriteViews views, WorkbenchSpritePlacement? placement)
    {
        var onToolbar = _slot == WorkbenchSpriteSlot.Toolbar && placement is { Fits: true } && _options is not null;
        var reserve = onToolbar ? placement!.CapsuleReserve : 0;
        if (!reserve.Equals(_reserve))
        {
            _reserve = reserve;
            views.Toolbar.SetCapsuleReserve(reserve);
        }
        if (onToolbar)
            views.Stage.HostLeft = placement!.BoxLeft - (_options!.Outer.Width - _options.Box.Width) / 2;   // 對齊用角色框，不含光暈
    }

    // ---- 計時器 ----

    private void UpdateRunning(WorkbenchSpriteViews views)
    {
        var shouldRun = _slot != WorkbenchSpriteSlot.None && views.Window.IsVisible
            && views.Window.WindowState != WindowState.Minimized && _host.IsEffectivelyVisible;
        if (shouldRun == _running)
            return;
        if (shouldRun)
        {
            _running = true;
            _frames!.Start();
            _poll.Start();
            UpdateFrameRate();
        }
        else
        {
            Stop();
        }
    }

    private void Stop()
    {
        _running = false;
        _frames?.Stop();
        _poll.Stop();
        _controller.Motion.Reset();   // 還沒換位置的瞬移會在這裡換好
        ApplyMotion();
    }

    internal void OnFrame(TimeSpan dt)
    {
        _controller.Motion.Update();
        ApplyMotion();
        _view?.Advance(dt);
        UpdateFrameRate();
    }

    /// <summary>每 100 ms：靠近與長按判定、完成回待機、休眠、探頭。</summary>
    internal void Poll()
    {
        if (_options is { } options)
        {
            Vector? fromCenter = null;
            if (_cursor is { } cursor && _views is { } views && _host.TranslatePoint(options.BoxRect.Center, views.Window) is { } center)
                fromCenter = cursor - center;
            _tracker.Poll(fromCenter, _shell.NearRadiusAt104 * options.Diameter / 104, keepNear: false);
        }
        _controller.Tick();
        UpdateFrameRate();
    }

    private void UpdateFrameRate()
    {
        if (_frames is null || _view is null || _views is not { } views)
            return;
        var fps = Math.Max(_view.PreferredFps, _controller.Motion.IsAnimating ? _shell.ActiveFps : 0);
        _frames.TargetFps = views.Window.IsActive ? fps : Math.Min(fps, _shell.SleepFps);
    }

    // ---- 狀態與動作 ----

    private void OnStateChanged(SpriteState state)
    {
        ApplyState();
        UpdateFrameRate();
    }

    private void ApplyState()
    {
        if (_view is null || _factory is null)
            return;
        var resolved = SpriteStateResolver.Resolve(_controller.State, _factory.Manifest);   // 殼負責解析，角色只會收到自己支援的狀態
        if (_applied == resolved)
            return;
        _applied = resolved;
        _view.SetState(resolved);
    }

    private void ApplyMotion() => _host.ApplyMotion(_controller.Motion.OffsetY, _controller.Motion.Opacity);

    // ---- 滑鼠 ----

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e) => _cursor = e.GetPosition(_views?.Window);

    private void OnWindowPointerExited(object? sender, PointerEventArgs e) => _cursor = null;

    private void OnHostPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_host);
        if (!point.Properties.IsLeftButtonPressed || _view?.HitTest(point.Position) != true)
            return;
        _pressAt = e.GetPosition(_views?.Window);
        e.Pointer.Capture(_host);
        _controller.Touch();
        _tracker.PointerPressed(point.Position);
        e.Handled = true;
    }

    private void OnHostMoved(object? sender, PointerEventArgs e)
    {
        if (_tracker.IsPressed)
            _tracker.PointerMoved(e.GetPosition(_views?.Window) - _pressAt);
    }

    private void OnHostReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tracker.IsPressed)
            return;
        _tracker.PointerReleased();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnHostCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _tracker.CaptureLost();

    /// <summary>拖曳事件不轉給角色：工作台的角色不能拖（plan/27 第 6.1 節 Q7）。</summary>
    private void OnTrackerEvent(TrackerEvent e)
    {
        switch (e)
        {
            case TrackerEvent.NearChanged near:
                _controller.SetNear(near.IsNear);
                _view?.OnInteraction(new SpriteInteraction.Near(near.IsNear));
                break;
            case TrackerEvent.Looking look:
                _view?.OnInteraction(new SpriteInteraction.Look(look.Direction));
                break;
            case TrackerEvent.PokeBegan poke:
                _view?.OnInteraction(new SpriteInteraction.PokeBegin(poke.At));
                break;
            case TrackerEvent.PokeEnded:
                _view?.OnInteraction(new SpriteInteraction.PokeEnd());
                break;
        }
    }
}
