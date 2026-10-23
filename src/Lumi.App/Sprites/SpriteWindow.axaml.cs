using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Sprites.Diagnostics;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Platform;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Sprites;

/// <summary>
/// 小精靈的殼：透明無框置頂視窗，裡面是角色、氣泡與輸入列。殼負責視窗、點擊穿透、靠近偵測、戳與拖曳的判定、
/// 幀率與位置記憶；角色（<see cref="ISpriteView"/>）只負責畫出自己（docs/sprite-dev-guide.md 第 1 節）。
/// 視窗大小固定（氣泡與輸入列的空間預留好），位置以角色中心為準。
/// </summary>
public partial class SpriteWindow : Window
{
    private const double InputBarHitMargin = 16;

    private readonly ISpriteCatalog _catalog;
    private readonly IModelCatalog _settings;
    private readonly SpriteShellOptions _shell;
    private readonly TimeProvider _time;
    private readonly ILogger<SpriteWindow> _logger;
    private readonly DeveloperOptions _developer;
    private readonly SpriteInteractionTracker _tracker;
    private readonly SpriteSleepPolicy _sleep;
    private readonly SpriteStateMapper _mapper;
    private readonly SpriteBubbleViewModel _bubble;
    private readonly IAppModeCoordinator? _coordinator;
    private readonly IChatSession? _session;
    private readonly ISettingsWindowService? _settingsWindow;
    private readonly IVoiceInputController _voice;
    private readonly SpriteVoiceBarViewModel _voiceBar;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PerfMonitor _perf = new();

    private IOverlayPlatform _overlay;
    private ISpriteFactory _factory = null!;
    private ISpriteView _view = null!;
    private SpriteOptions _spriteOptions = null!;
    private SpriteSize _builtSize;
    private SpriteLayout _layout = null!;
    private FrameDriver? _frames;

    private SpriteState _requested = SpriteState.Idle;
    private SpriteState? _applied;
    private bool _inputShown;
    private bool _bubbleShown;
    private VoiceInputState _focusedVoiceState = VoiceInputState.Idle;
    private bool _responding;
    private bool _menuOpen;
    private bool _pointerInside;
    private bool _clickThroughEnabled = true;
    private string? _initError;
    private Point? _lastCursor;
    private DateTimeOffset _fastUntil = DateTimeOffset.MinValue;

    private PixelPoint _pressScreen;
    private PixelPoint _pressWindow;

    public SpriteWindow(
        ISpriteCatalog catalog,
        IOverlayPlatform overlay,
        IModelCatalog settings,
        SpriteShellOptions shell,
        TimeProvider time,
        ILogger<SpriteWindow> logger,
        DeveloperOptions? developer = null,
        SpriteStateMapper? mapper = null,
        SpriteBubbleViewModel? bubble = null,
        IAppModeCoordinator? coordinator = null,
        IChatSession? session = null,
        ISettingsWindowService? settingsWindow = null,
        IVoiceInputController? voice = null,
        NotificationCenter? notifications = null)
    {
        _catalog = catalog;
        _overlay = overlay;
        _settings = settings;
        _shell = shell;
        _time = time;
        _logger = logger;
        _developer = developer ?? new DeveloperOptions();
        _tracker = new SpriteInteractionTracker(shell, time);
        _sleep = new SpriteSleepPolicy(shell, time);
        _mapper = mapper ?? new SpriteStateMapper(shell, time);
        _bubble = bubble ?? new SpriteBubbleViewModel(shell, time);
        _coordinator = coordinator;
        _session = session;
        _settingsWindow = settingsWindow;
        _voice = voice ?? NoVoiceInputController.Instance;
        _pollTimer = new DispatcherTimer { Interval = shell.PollInterval };

        InitializeComponent();
        _tracker.Raised += OnTrackerEvent;
        Bubble.DataContext = _bubble;

        // 語音：輸入列的麥克風與收音列（ViewModel），狀態改變時把焦點放到該狀態的預設按鈕
        _voiceBar = new SpriteVoiceBarViewModel(_voice);
        InputBar.DataContext = _voiceBar;
        Input.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)   // 用屬性變更而不是 TextChanged：程式設定的文字也要拿掉語音標籤
                _voiceBar.OnInputEdited(Input.Text ?? "");
        };
        _voiceBar.PropertyChanged += (_, _) => OnVoiceStateMaybeChanged();
        _mapper.Changed += SetState;
        _bubble.Dismissed += _mapper.Acknowledge;   // 錯誤的氣泡被關掉：角色離開 Error
        if (notifications is not null)
            _bubble.BindReminders(notifications.Items);   // 提醒卡片綁定通知清單（M10 10E）
        // 設定頁換了角色或尺寸、或角色資料夾重新載入了：立即生效（在 UI 執行緒上重建角色，位置以角色中心為準不跳動）
        _settings.Changed += (_, _) => PostRebuild();
        if (_catalog is IReloadableSpriteCatalog reloadable)
            reloadable.Changed += (_, _) => PostRebuild();
        BuildSprite(_settings.Current.Sprite);

        SpriteHost.PointerPressed += OnSpritePressed;
        SpriteHost.PointerMoved += OnSpriteMoved;
        SpriteHost.PointerReleased += OnSpriteReleased;
        SpriteHost.PointerCaptureLost += OnSpriteCaptureLost;
        SpriteHost.ContextMenu = BuildMenu();

        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        SendButton.Click += (_, _) =>
        {
            if (_responding)
                StopRequested?.Invoke();
            else
                SubmitInput();
        };
        KeyDown += OnWindowKeyDown;
        PointerEntered += (_, _) => _pointerInside = true;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                _voiceBar.Shown = IsVisible;   // 隱藏時收音會停止（不在背景錄音）；麥克風停用並說明原因
        };
        PointerExited += (_, _) => _pointerInside = false;

        _pollTimer.Tick += (_, _) => Poll();
        _perfTimer.Tick += (_, _) =>
        {
            _perf.Sample(_frames?.Frames ?? 0);
            UpdateHud();
        };

        Hud.IsVisible = _developer.ShowSpriteHud;
        Opened += OnOpened;
        Closing += (_, _) => StopTimers();
        // 隱藏時（暫時隱藏、工作台模式）不要白白輪詢與繪圖；再顯示時恢復
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || _frames is null)
                return;
            if (IsVisible)
                StartTimers();
            else
                PauseTimers();
        };
    }

    // ---- 給對話與示範流程用的介面 ----

    /// <summary>使用者在輸入列按 Enter 或送出鈕。</summary>
    public event Action<string>? InputSubmitted;

    /// <summary>短按角色（沒有戳、沒有拖）。</summary>
    public event Action? SpriteClicked;

    /// <summary>目前要求的狀態（休眠由殼自己疊上去）。</summary>
    public SpriteState State => _requested;

    /// <summary>角色實際收到的（已解析退化、疊上休眠）狀態。</summary>
    public SpriteState? AppliedState => _applied;

    public bool IsNear => _tracker.IsNear;

    /// <summary>設定要求的狀態；角色不支援的會依退化規則換成支援的，休眠由殼疊上去。</summary>
    public void SetState(SpriteState state)
    {
        _requested = state;
        ApplyState();
    }

    /// <summary>回應中按下送出鈕（這時它是「停止」）。</summary>
    public event Action? StopRequested;

    /// <summary>回應中：輸入列仍可打草稿，送出鈕換成停止（ui-spec 2.2）。</summary>
    public void SetResponding(bool responding)
    {
        _responding = responding;
        SendIcon.IsVisible = !responding;
        StopIcon.IsVisible = responding;
        SendButton.Classes.Set("send", !responding);   // 純視覺：送出 = Accent 底，停止 = Primary 底（Button.send／Button.stop 在 Themes/Controls.axaml）
        SendButton.Classes.Set("stop", responding);
        AutomationProperties.SetName(SendButton, responding ? Strings.Sprite_Stop : Strings.Sprite_Send);
        ToolTip.SetTip(SendButton, responding ? Strings.Sprite_Stop : Strings.Sprite_Send);
    }

    internal SpriteBubbleViewModel BubbleViewModel => _bubble;

    internal SpriteStateMapper Mapper => _mapper;

    // ---- 建立角色與版面 ----

    private static double DiameterOf(SpriteSize size) => size switch
    {
        SpriteSize.Small => SpriteLimits.Tiers[0],
        SpriteSize.Large => SpriteLimits.Tiers[^1],
        _ => SpriteLimits.DefaultTier,
    };

    private void BuildSprite(SpriteSettings settings)
    {
        _factory = _catalog.Find(settings.SpriteId) ?? _catalog.All.FirstOrDefault()
            ?? throw new InvalidOperationException("沒有任何已註冊的小精靈角色。");
        var fit = SpriteLimits.Fit(_factory.Manifest, DiameterOf(settings.Size));
        foreach (var warning in fit.Warnings)
            _logger.LogWarning("{Warning}", warning);

        _builtSize = settings.Size;
        _spriteOptions = fit.Options;
        _view = _factory.Create(_spriteOptions);
        SpriteHost.Child = _view.Visual;
        _applied = null;   // 新的角色還沒收過狀態
        _layout = SpriteLayout.Compute(_spriteOptions, _view.BubbleAnchor, _shell);
        ApplyLayout();
        ApplyState();
    }

    private void ApplyLayout()
    {
        Width = _layout.WindowSize.Width;
        Height = _layout.WindowSize.Height;
        SpriteHost.Margin = new Thickness(_layout.SpriteOrigin.X, _layout.SpriteOrigin.Y, 0, 0);
        SpriteHost.Width = _spriteOptions.Outer.Width;
        SpriteHost.Height = _spriteOptions.Outer.Height;
        BubbleArea.Height = _layout.BubbleBottom;
        InputBar.Margin = new Thickness(_layout.InputBar.X, _layout.InputBar.Y, 0, 0);
        InputBar.Width = _layout.InputBar.Width;
        InputBar.Height = _layout.InputBar.Height;
    }

    internal SpriteLayout Layout => _layout;

    internal ISpriteView View => _view;

    /// <summary>角色中心在螢幕上的位置（螢幕像素）。</summary>
    internal PixelPoint SpriteCenterOnScreen => _layout.SpriteCenterOnScreen(Position, DesktopScaling);

    /// <summary>換尺寸檔位：寫進設定，視窗重算、角色重建，位置以角色中心為準不跳動。</summary>
    public async Task SetSizeAsync(SpriteSize size)
    {
        var current = _settings.Current.Sprite;
        if (current.Size != size)
            await _settings.SetSpriteAsync(current with { Size = size });
        RebuildIfSettingsChanged();
    }

    private void PostRebuild()
    {
        if (Dispatcher.UIThread.CheckAccess())
            RebuildIfSettingsChanged();
        else
            Dispatcher.UIThread.Post(RebuildIfSettingsChanged);
    }

    /// <summary>
    /// 想要的角色（設定裡的 id，找不到就退回第一個）和已經建好的不同才重建：換了角色、換了尺寸，
    /// 或重新載入之後同一個角色有了新的工廠（圖片可能改過）。設定的其他變更（主題、人設…）不會動到角色。
    /// 選用的角色被移走時退回第一個，設定裡的選擇不動，角色回來之後重新載入就自動換回去。
    /// </summary>
    private void RebuildIfSettingsChanged()
    {
        var wanted = _settings.Current.Sprite;
        var wantedFactory = _catalog.Find(wanted.SpriteId) ?? _catalog.All.FirstOrDefault();
        if (ReferenceEquals(wantedFactory, _factory) && wanted.Size == _builtSize)
            return;

        var center = SpriteCenterOnScreen;
        BuildSprite(wanted);
        MoveCenterTo(center);
        ClampToScreens();
    }

    private void MoveCenterTo(PixelPoint center) => Position = _layout.WindowPositionFor(center, DesktopScaling);

    // ---- 開啟 ----

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _overlay.Attach(TryGetPlatformHandle()?.Handle ?? 0);
        }
        catch (Exception ex)
        {
            _initError = $"原生初始化失敗：{ex.Message}";
            _logger.LogWarning(ex, "小精靈視窗的原生初始化失敗，改用退化模式");
            _overlay = new NullOverlay();
        }

        PlaceInitially();
        _frames = new FrameDriver(this, dt => _view.Advance(dt));
        StartTimers();
    }

    private void StartTimers()
    {
        _frames?.Start();
        _pollTimer.Start();
        _perfTimer.Start();
    }

    private void PauseTimers()
    {
        _frames?.Stop();
        _pollTimer.Stop();
        _perfTimer.Stop();
    }

    private void StopTimers() => PauseTimers();

    private IReadOnlyList<ScreenArea> Areas() =>
        [.. Screens.All.Select(s => new ScreenArea(s.WorkingArea, s.IsPrimary))];

    private double HalfBoxPx => Math.Max(_spriteOptions.Box.Width, _spriteOptions.Box.Height) / 2 * DesktopScaling;

    /// <summary>上次的位置；那個點已經不在任何螢幕內（拔掉外接螢幕）或從沒拖過，改放主螢幕右下角。</summary>
    private void PlaceInitially()
    {
        var areas = Areas();
        if (areas.Count == 0)
            return;

        var saved = _settings.Current.Sprite.Placement;
        var center = saved is null
            ? SpritePlacementRules.DefaultCenter(areas.FirstOrDefault(a => a.IsPrimary, areas[0]).WorkingArea, HalfBoxPx)
            : SpritePlacementRules.Clamp(new PixelPoint(saved.CenterX, saved.CenterY), areas, HalfBoxPx);
        MoveCenterTo(center);
    }

    /// <summary>放開拖曳後，確保角色完整留在某一個螢幕的可用範圍內（多螢幕交界）。</summary>
    private void ClampToScreens()
    {
        var areas = Areas();
        if (areas.Count == 0)
            return;
        MoveCenterTo(SpritePlacementRules.Clamp(SpriteCenterOnScreen, areas, HalfBoxPx));
    }

    private void SavePlacement()
    {
        var center = SpriteCenterOnScreen;
        var placement = new SpritePlacement(center.X, center.Y);
        _ = SavePlacementAsync(placement);
    }

    private async Task SavePlacementAsync(SpritePlacement placement)
    {
        try
        {
            await _settings.SetSpriteAsync(_settings.Current.Sprite with { Placement = placement });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "儲存小精靈位置失敗");
        }
    }

    // ---- 輪詢：靠近偵測、點擊穿透、狀態、幀率（每 33 ms） ----

    private double NearRadius => _shell.NearRadiusAt104 * _spriteOptions.Diameter / 104;

    internal void Poll()
    {
        Point? cursor = null;
        if (_overlay.IsSupported && _overlay.TryGetCursorInWindow(RenderScaling, ClientSize.Height, out var p))
            cursor = new Point(p.X, p.Y);
        _lastCursor = cursor;

        var typing = Input.IsFocused && !string.IsNullOrEmpty(Input.Text);
        var overInput = cursor is { } c && _inputShown && _layout.InputBar.Inflate(InputBarHitMargin).Contains(c);
        Vector? fromCenter = cursor is { } at ? at - _layout.SpriteCenter : null;
        _tracker.Poll(fromCenter, NearRadius, typing || overInput, _pointerInside);

        _mapper.Tick();
        _bubble.SetHovered(_bubble.IsVisible && (cursor is { } hover ? BubbleRect().Contains(hover) : Bubble.IsPointerOver));
        _bubble.Tick();

        if (cursor is { } cur && _clickThroughEnabled && _overlay.IsSupported)
            _overlay.SetClickThrough(!(_tracker.IsPressed || _menuOpen || IsInteractiveAt(cur)));

        ApplyState();
        UpdateChrome();
        UpdateFrameRate();
    }

    /// <summary>游標在「可互動區」才關閉穿透：角色本體、已浮現的輸入列、HUD。沒有按鈕的氣泡維持穿透。</summary>
    private Rect BubbleRect()
    {
        Control target = Bubble.FindControl<Border>("BubbleBorder") ?? (Control)Bubble;
        return new Rect(target.TranslatePoint(new Point(0, 0), this) ?? default, target.Bounds.Size);
    }

    /// <summary>提醒卡片的關閉鈕所在的矩形（視窗座標）。</summary>
    private Rect ReminderButtonRect()
    {
        var button = Bubble.FindControl<Button>("ReminderDismissButton")!;
        return new Rect(button.TranslatePoint(new Point(0, 0), this) ?? default, button.Bounds.Size);
    }

    private bool IsInteractiveAt(Point p)
    {
        if (_bubble.HasReminder && ReminderButtonRect().Contains(p))
            return true;   // 提醒的關閉鈕；卡片的其他部分穿透
        if (_bubble.IsVisible && _bubble.IsInteractive && BubbleRect().Contains(p))
            return true;   // 有按鈕的氣泡（確認、連結、重試）；沒有按鈕的氣泡維持穿透
        if (_view.HitTest(new Point(p.X - _layout.SpriteOrigin.X, p.Y - _layout.SpriteOrigin.Y)))
            return true;
        if (_inputShown && _layout.InputBar.Contains(p))
            return true;
        return Hud.IsVisible && new Rect(Hud.Bounds.Position, Hud.Bounds.Size).Contains(p);
    }

    private void ApplyState()
    {
        var desired = _sleep.Update(_requested, _tracker.IsNear);
        var resolved = SpriteStateResolver.Resolve(desired, _factory.Manifest);   // 殼負責解析，角色只會收到自己支援的狀態
        if (_applied == resolved)
            return;
        _applied = resolved;
        _view.SetState(resolved);
    }

    private void UpdateFrameRate()
    {
        if (_frames is null)
            return;
        // 角色自己要的幀率，和殼的需求（氣泡與輸入列的動畫中不降）取大者
        var shellNeed = _time.GetUtcNow() < _fastUntil ? _shell.ActiveFps : 0;
        _frames.TargetFps = Math.Max(_view.PreferredFps, shellNeed);
    }

    // ---- 輸入列與氣泡 ----

    private void UpdateChrome()
    {
        var typing = Input.IsFocused && !string.IsNullOrEmpty(Input.Text);
        var showInput = _tracker.IsNear || _applied is SpriteState.Listening or SpriteState.Transcribing || typing || _voiceBar.IsVoiceActive;
        SetShown(InputBar, ref _inputShown, showInput, 12, interactive: true);
        if (!showInput && Input.IsFocused)
            Focus();

        // 提醒卡片（只有關閉鈕可以點）與回應氣泡共用同一個容器；兩者都沒有就整個隱藏
        BubbleArea.IsHitTestVisible = _bubble.HasReminder || (_bubble.IsVisible && _bubble.IsInteractive);
        SetShown(Bubble, ref _bubbleShown, _bubble.IsVisible || _bubble.HasReminder, 8, interactive: false);
    }

    private void SetShown(Control control, ref bool current, bool show, double hiddenOffset, bool interactive)
    {
        if (current == show)
            return;
        current = show;
        control.Opacity = show ? 1 : 0;
        control.RenderTransform = TransformOperations.Parse(show ? "translateY(0px)" : $"translateY({hiddenOffset.ToString(CultureInfo.InvariantCulture)}px)");
        control.IsHitTestVisible = show && interactive;   // 氣泡永遠不攔截滑鼠
        _fastUntil = _time.GetUtcNow() + TimeSpan.FromMilliseconds(400);
    }

    private void SubmitInput()
    {
        var text = Input.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        Input.Text = "";
        _sleep.Touch();
        InputSubmitted?.Invoke(text);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 輸入法組字中的 Enter 由 IME 處理，不會走到這裡；回應中只能打草稿，不能送出
            if (!_responding)
                SubmitInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Input.Text = "";
            Focus();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1 && _developer.ShowSpriteHud)
        {
            Hud.IsVisible = !Hud.IsVisible;
            UpdateHud();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _voiceBar.CancelRecordingIfAny())
        {
            e.Handled = true;   // Esc＝取消錄音（ui-spec 6.3）
        }
    }

    // ---- 語音：焦點跟著狀態走（ui-spec 6.3；讀屏軟體靠焦點讀出狀態） ----

    /// <summary>VM 的屬性每次更新都會通知（計時、音量也是），所以只在狀態真的改變時搬移焦點。</summary>
    private void OnVoiceStateMaybeChanged()
    {
        var state = _voice.State;
        if (state == _focusedVoiceState)
            return;
        var previous = _focusedVoiceState;
        _focusedVoiceState = state;

        if (state == VoiceInputState.Idle)
        {
            if (previous != VoiceInputState.Idle)
                Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Loaded);   // 回到閒置：回到輸入框
            return;
        }
        // 延後一拍：收音列剛顯示時 Focus() 會回傳 false
        Dispatcher.UIThread.Post(() => VoiceBar.FocusDefault(), DispatcherPriority.Loaded);
    }

    // ---- 語音與輸入框（給小精靈的語音接線用） ----

    /// <summary>目前是否顯示中（隱藏時語音結果只放進輸入框，不送出）。</summary>
    internal bool IsShown => IsVisible;

    /// <summary>輸入框目前的文字。</summary>
    internal string InputText => Input.Text ?? "";

    /// <summary>
    /// 語音結果放進輸入框（停止並編輯，或送出時被規則擋下來）：輸入框已有草稿就接在後面（輸入框只有一行，用空白分隔），
    /// 前面顯示「語音」標籤，直到使用者改動文字為止。
    /// </summary>
    internal void InsertVoiceDraft(string text)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var draft = Input.Text ?? "";
        var combined = draft.Trim().Length == 0 ? flat : draft.TrimEnd() + " " + flat;
        Input.Text = combined;
        _voiceBar.MarkDraft(combined);
        Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Loaded);
    }

    // ---- 指標：按下、移動、放開 ----

    private void OnSpritePressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(SpriteHost);
        if (!point.Properties.IsLeftButtonPressed || !_view.HitTest(point.Position))
            return;

        _pressScreen = this.PointToScreen(e.GetPosition(this));
        _pressWindow = Position;
        e.Pointer.Capture(SpriteHost);
        _sleep.Touch();
        _tracker.PointerPressed(point.Position);
        e.Handled = true;
    }

    private void OnSpriteMoved(object? sender, PointerEventArgs e)
    {
        if (!_tracker.IsPressed)
            return;
        var current = this.PointToScreen(e.GetPosition(this));
        _tracker.PointerMoved(new Vector((current.X - _pressScreen.X) / DesktopScaling, (current.Y - _pressScreen.Y) / DesktopScaling));
    }

    private void OnSpriteReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tracker.IsPressed)
            return;
        _tracker.PointerReleased();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnSpriteCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _tracker.CaptureLost();

    private void OnTrackerEvent(TrackerEvent e)
    {
        switch (e)
        {
            case TrackerEvent.NearChanged near:
                _view.OnInteraction(new SpriteInteraction.Near(near.IsNear));
                break;
            case TrackerEvent.Looking look:
                _view.OnInteraction(new SpriteInteraction.Look(look.Direction));
                break;
            case TrackerEvent.PokeBegan poke:
                _view.OnInteraction(new SpriteInteraction.PokeBegin(poke.At));
                break;
            case TrackerEvent.PokeEnded:
                _view.OnInteraction(new SpriteInteraction.PokeEnd());
                break;
            case TrackerEvent.DragBegan:
                _view.OnInteraction(new SpriteInteraction.DragBegin());
                break;
            case TrackerEvent.DragMoved moved:
                Position = new PixelPoint(
                    _pressWindow.X + (int)Math.Round(moved.Delta.X * DesktopScaling),
                    _pressWindow.Y + (int)Math.Round(moved.Delta.Y * DesktopScaling));
                break;
            case TrackerEvent.DragEnded:
                _view.OnInteraction(new SpriteInteraction.DragEnd());
                ClampToScreens();
                SavePlacement();
                break;
            case TrackerEvent.Clicked:
                _sleep.Touch();
                _mapper.Acknowledge();
                SpriteClicked?.Invoke();
                break;
        }
    }

    // ---- 右鍵選單（ui-spec 8.5） ----

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;

        var openWorkbench = new MenuItem { Header = Strings.Sprite_MenuOpenWorkbench };
        openWorkbench.Click += (_, _) => _coordinator?.OpenWorkbench();

        var newConversation = new MenuItem { Header = Strings.Sprite_MenuNewConversation };
        newConversation.Click += async (_, _) => await NewConversationAsync();

        var switchSprite = new MenuItem { Header = Strings.Sprite_MenuSwitchSprite };
        switchSprite.Click += (_, _) => _ = _settingsWindow?.ShowAsync(new SettingsNavigation(SettingsSection.Sprite));

        var settings = new MenuItem { Header = Strings.Sprite_MenuSettings };
        settings.Click += (_, _) => _ = _settingsWindow?.ShowAsync(new SettingsNavigation(SettingsSection.Connections));

        var hide = new MenuItem { Header = Strings.Sprite_MenuHide };
        hide.Click += (_, _) => _coordinator?.HideSpriteTemporarily();

        var exit = new MenuItem { Header = Strings.Sprite_MenuExit };
        exit.Click += (_, _) => _coordinator?.Exit();

        // 回應中：「開啟工作台」可用（以查看中狀態開啟），「新對話」停用
        menu.Opening += (_, _) =>
        {
            newConversation.IsEnabled = _session is not null && !_session.IsResponding;
            openWorkbench.IsEnabled = _coordinator is not null;
        };

        menu.Items.Add(openWorkbench);
        menu.Items.Add(newConversation);
        menu.Items.Add(new Separator());
        menu.Items.Add(switchSprite);
        menu.Items.Add(settings);
        menu.Items.Add(hide);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        if (_developer.ShowSpriteHud)
            AddDeveloperItems(menu);
        return menu;
    }

    private async Task NewConversationAsync()
    {
        if (_session is null || _session.IsResponding)
            return;
        try
        {
            await _session.NewConversationAsync(Strings.Workbench_NewConversationTitle);
            _bubble.Clear();
            _mapper.Acknowledge();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "小精靈建立新對話失敗");
        }
    }

    /// <summary>開發用（<c>Lumi:Developer:ShowSpriteHud</c>）：切換狀態預覽、尺寸、HUD、點擊穿透。</summary>
    private void AddDeveloperItems(ContextMenu menu)
    {
        var states = new MenuItem { Header = "開發：狀態" };
        foreach (var state in Enum.GetValues<SpriteState>())
        {
            var item = new MenuItem { Header = state.ToString() };
            item.Click += (_, _) => SetState(state);
            states.Items.Add(item);
        }

        var size = new MenuItem { Header = "開發：大小" };
        foreach (var (value, label) in new[] { (SpriteSize.Small, "小（64）"), (SpriteSize.Medium, "中（80）"), (SpriteSize.Large, "大（104）") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await SetSizeAsync(value);
            size.Items.Add(item);
        }

        var clickThrough = new MenuItem();
        clickThrough.Click += (_, _) =>
        {
            _clickThroughEnabled = !_clickThroughEnabled;
            if (!_clickThroughEnabled)
                _overlay.SetClickThrough(false);
        };
        var hud = new MenuItem();
        hud.Click += (_, _) =>
        {
            Hud.IsVisible = !Hud.IsVisible;
            UpdateHud();
        };
        var resetAverage = new MenuItem { Header = "開發：重設 CPU 平均" };
        resetAverage.Click += (_, _) => _perf.ResetAverage();

        menu.Opening += (_, _) =>
        {
            clickThrough.Header = "開發：" + (_clickThroughEnabled ? "✓  " : "    ") + "點擊穿透透明區域";
            clickThrough.IsEnabled = _overlay.IsSupported;
            hud.Header = "開發：" + (Hud.IsVisible ? "✓  " : "    ") + "診斷 HUD";
        };

        menu.Items.Add(new Separator());
        menu.Items.Add(states);
        menu.Items.Add(size);
        menu.Items.Add(hud);
        menu.Items.Add(clickThrough);
        menu.Items.Add(resetAverage);
    }

    // ---- 診斷 HUD ----

    private void UpdateHud()
    {
        if (!Hud.IsVisible)
            return;
        var cursor = _lastCursor is { } c ? $"{c.X:0},{c.Y:0}" : "無法取得（退化為視窗內偵測）";
        var clickThrough = !_overlay.IsSupported ? "不支援"
            : !_clickThroughEnabled ? "關閉"
            : _overlay.ClickThroughActive ? "穿透中" : "可互動";
        HudText.Text =
            $"{_overlay.Name}\n" +
            $"FPS {_perf.Fps:0}（目標 {_frames?.TargetFps}）  縮放 {RenderScaling:0.##}/{DesktopScaling:0.##}\n" +
            $"CPU {_perf.CpuOneCore:0.0}%  30 秒平均 {_perf.CpuAverage30:0.0}%（單核）\n" +
            $"記憶體 {_perf.WorkingSetMb:0} MB\n" +
            $"狀態 {_applied}（要求 {_requested}）  靠近 {(_tracker.IsNear ? "是" : "否")}  拖曳 {(_tracker.IsDragging ? "是" : "否")}  戳 {(_tracker.IsPoking ? "是" : "否")}\n" +
            $"游標 {cursor}\n" +
            $"點擊穿透 {clickThrough}" +
            (_initError is null ? "" : $"\n{_initError}");
    }
}
