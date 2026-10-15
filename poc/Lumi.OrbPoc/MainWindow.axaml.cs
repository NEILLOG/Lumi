using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Lumi.OrbPoc.Diagnostics;
using Lumi.OrbPoc.Platform;

namespace Lumi.OrbPoc;

public partial class MainWindow : Window
{
    const double NearRadiusAt104 = 150;   // DIP：直徑 104 時，游標距光球中心多近算「靠近」（隨大小等比縮放）
    double NearRadius => NearRadiusAt104 * Orb.OrbDiameter / 104;
    const double NearHoldSeconds = 0.6;   // 離開後延遲收起，避免閃爍
    const double LongPressSeconds = 0.45; // 長按多久算「戳」
    const double DragThresholdDip = 4;    // 位移超過多少算「拖曳」
    const double AutoSleepSeconds = 60;   // 待機多久進入休眠

    IOverlayPlatform _plat = new NullOverlay();
    FrameDriver? _frames;
    readonly PerfMonitor _perf = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _longPress = new() { Interval = TimeSpan.FromSeconds(LongPressSeconds) };

    bool _clickThroughEnabled = !App.HasArg("--no-clickthrough");
    bool _adaptiveFps = true;
    bool _systemDrag;
    string? _initError;

    bool _near, _inputShown, _bubbleShown, _menuOpen, _pointerInsideFallback;
    double _lastNearAt = -10, _idleSince;
    Point? _lastCursor;
    string? _bubbleText, _renderedBubble;
    int _flowId, _bubbleToken;

    bool _pressed, _dragging;
    Point _pressLocal;
    PixelPoint _pressScreen, _pressWinPos;
    PointerPressedEventArgs? _pressArgs;

    double Now => _clock.Elapsed.TotalSeconds;

    public MainWindow()
    {
        InitializeComponent();
        SetOrbSize(80);

        Orb.PointerPressed += OnOrbPressed;
        Orb.PointerMoved += OnOrbMoved;
        Orb.PointerReleased += OnOrbReleased;
        Orb.PointerCaptureLost += OnOrbCaptureLost;
        Orb.ContextMenu = BuildMenu();

        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        MicButton.Click += (_, _) => _ = RunVoiceDemo();
        KeyDown += OnWindowKeyDown;
        PointerEntered += (_, _) => _pointerInsideFallback = true;
        PointerExited += (_, _) => _pointerInsideFallback = false;

        _longPress.Tick += (_, _) =>
        {
            _longPress.Stop();
            if (_pressed && !_dragging)
            {
                Orb.BeginPoke(_pressLocal);
                ShowBubble("唔…！");
            }
        };
        _poll.Tick += (_, _) => Poll();
        _perfTimer.Tick += (_, _) =>
        {
            _perf.Sample(_frames?.Frames ?? 0);
            UpdateHud();
        };

        Opened += OnOpened;
        Closing += (_, _) =>
        {
            _frames?.Stop();
            _poll.Stop();
            _perfTimer.Stop();
        };
    }

    void OnOpened(object? sender, EventArgs e)
    {
        _plat = OverlayPlatform.Create(this);
        try { _plat.Initialize(); }
        catch (Exception ex)
        {
            _initError = $"原生初始化失敗：{ex.Message}";
            _plat = new NullOverlay();
        }

        PlaceBottomRight();
        _frames = new FrameDriver(this, dt => Orb.Advance(dt));
        _frames.Start();
        _poll.Start();
        _perfTimer.Start();
        _idleSince = Now;
    }

    // ───────────────── 靠近偵測 + 點擊穿透（每 33ms 輪詢）─────────────────
    void Poll()
    {
        double now = Now;
        bool rawNear;

        if (_plat.SupportsCursorPolling && _plat.TryGetCursorInWindow(out var cursor))
        {
            _lastCursor = cursor;
            var center = Orb.TranslatePoint(Orb.Center, this) ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
            var d = cursor - center;
            double dist = Math.Sqrt(d.X * d.X + d.Y * d.Y);

            rawNear = dist < NearRadius || (_inputShown && RectOf(InputBar).Inflate(16).Contains(cursor));
            Orb.LookTarget = new Vector(Math.Clamp(d.X / NearRadius, -1, 1), Math.Clamp(d.Y / NearRadius, -1, 1));

            if (_clickThroughEnabled && _plat.SupportsClickThrough)
            {
                bool interactive = _pressed || _menuOpen || IsInteractiveAt(cursor);
                _plat.SetClickThrough(!interactive);
            }
        }
        else
        {
            _lastCursor = null;
            rawNear = _pointerInsideFallback;   // 退化：只能在視窗內才偵測得到
        }

        bool typing = Input.IsFocused && !string.IsNullOrEmpty(Input.Text);
        if (rawNear || typing || _pressed) _lastNearAt = now;
        _near = now - _lastNearAt < NearHoldSeconds;
        Orb.Near = _near;

        // 自動休眠 / 喚醒
        if (Orb.State == OrbState.Idle && !_near)
        {
            if (now - _idleSince > AutoSleepSeconds) Orb.SetState(OrbState.Sleep);
        }
        else if (Orb.State != OrbState.Sleep)
        {
            _idleSince = now;
        }
        if (Orb.State == OrbState.Sleep && _near)
        {
            Orb.SetState(OrbState.Idle);
            _idleSince = now;
        }

        UpdateChrome();
        UpdateTargetFps();
    }

    Rect RectOf(Control c)
    {
        var p = c.TranslatePoint(new Point(0, 0), this) ?? default;
        return new Rect(p, c.Bounds.Size);
    }

    bool IsInteractiveAt(Point p)
    {
        var orbTopLeft = Orb.TranslatePoint(new Point(0, 0), this) ?? default;
        if (Orb.HitOrb(new Point(p.X - orbTopLeft.X, p.Y - orbTopLeft.Y))) return true;
        if (_inputShown && RectOf(InputBar).Contains(p)) return true;
        if (Hud.IsVisible && RectOf(Hud).Contains(p)) return true;
        return false;
    }

    // ───────────────── 輸入列與氣泡的顯示 ─────────────────
    void UpdateChrome()
    {
        bool showInput = _near
            || Orb.State is OrbState.Listening or OrbState.Transcribing
            || (Input.IsFocused && !string.IsNullOrEmpty(Input.Text));
        SetShown(InputBar, ref _inputShown, showInput, 12, interactive: true);
        if (!showInput && Input.IsFocused) Focus();

        string? text = _bubbleText ?? (_near && Orb.State == OrbState.Idle ? "嗨，有什麼想問的嗎？" : null);
        if (text is not null && text != _renderedBubble) { EmojiText.Apply(BubbleText, text); _renderedBubble = text; }
        SetShown(Bubble, ref _bubbleShown, text is not null, 8, interactive: false);
    }

    static void SetShown(Control c, ref bool current, bool show, double hiddenOffset, bool interactive)
    {
        if (current == show) return;
        current = show;
        c.Opacity = show ? 1 : 0;
        c.RenderTransform = TransformOperations.Parse(show ? "translateY(0px)" : $"translateY({hiddenOffset}px)");
        c.IsHitTestVisible = show && interactive;   // 氣泡永遠不攔截滑鼠
    }

    void ShowBubble(string? text)
    {
        _bubbleToken++;
        _bubbleText = text;
        UpdateChrome();
    }

    async Task FlashBubble(string text, int ms)
    {
        ShowBubble(text);
        int token = _bubbleToken;
        await Task.Delay(ms);
        if (token == _bubbleToken) ShowBubble(null);
    }

    // ───────────────── 幀率策略（耗能驗證）─────────────────
    void UpdateTargetFps()
    {
        if (_frames is null) return;
        int target = 60;
        if (_adaptiveFps)
        {
            bool calmIdle = Orb.State == OrbState.Idle && !_near && !_pressed && !Orb.IsPoking && !Orb.IsSettling;
            target = Orb.State == OrbState.Sleep ? 12 : calmIdle ? 30 : 60;
        }
        _frames.TargetFps = target;
    }

    // ───────────────── 長按戳 vs 拖曳 ─────────────────
    void OnOrbPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Orb);
        if (!pt.Properties.IsLeftButtonPressed || !Orb.HitOrb(pt.Position)) return;

        _pressed = true;
        _dragging = false;
        _pressLocal = pt.Position;
        _pressArgs = e;
        _pressScreen = this.PointToScreen(e.GetPosition(this));
        _pressWinPos = Position;
        e.Pointer.Capture(Orb);
        _longPress.Stop();
        _longPress.Start();
        e.Handled = true;
    }

    void OnOrbMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        var cur = this.PointToScreen(e.GetPosition(this));
        int dx = cur.X - _pressScreen.X, dy = cur.Y - _pressScreen.Y;

        if (!_dragging)
        {
            if (Orb.IsPoking) return;   // 已經戳住就不轉成拖曳
            double thr = DragThresholdDip * DesktopScaling;
            if (Math.Abs(dx) < thr && Math.Abs(dy) < thr) return;

            _dragging = true;
            _longPress.Stop();
            Orb.Dragging = true;

            if (_systemDrag && _pressArgs is not null)
            {
                var args = _pressArgs;
                _pressed = false;
                e.Pointer.Capture(null);
                BeginMoveDrag(args);   // 交給系統拖曳（比較用）
                EndDrag();
                return;
            }
        }

        Position = new PixelPoint(_pressWinPos.X + dx, _pressWinPos.Y + dy);
    }

    void OnOrbReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _longPress.Stop();

        if (_dragging) EndDrag();
        else if (Orb.IsPoking)
        {
            Orb.EndPoke();
            _ = FlashBubble("彈～", 1400);
        }
        else OnOrbClick();

        _pressArgs = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    void OnOrbCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _longPress.Stop();
        if (_dragging) EndDrag();
        if (Orb.IsPoking)
        {
            Orb.EndPoke();
            ShowBubble(null);
        }
        _pressArgs = null;
    }

    void EndDrag()
    {
        _dragging = false;
        Orb.Dragging = false;
        ClampToScreen();
    }

    static readonly OrbState[] DemoOrder =
    {
        OrbState.Idle, OrbState.Listening, OrbState.Transcribing, OrbState.Thinking, OrbState.Tool,
        OrbState.Streaming, OrbState.Done, OrbState.Attention, OrbState.Error, OrbState.Sleep,
    };

    void OnOrbClick()
    {
        if (Orb.State == OrbState.Sleep)
        {
            SetDemoState(OrbState.Idle);
            return;
        }
        int i = Array.IndexOf(DemoOrder, Orb.State);
        SetDemoState(DemoOrder[(i + 1) % DemoOrder.Length]);
    }

    void SetDemoState(OrbState s)
    {
        _flowId++;   // 中止進行中的示範流程
        Orb.SetState(s);
        _idleSince = Now;
        ShowBubble(DemoText(s));
    }

    static string? DemoText(OrbState s) => s switch
    {
        OrbState.Listening => "我在聽…",
        OrbState.Transcribing => "「明天台中會下雨嗎？」",
        OrbState.Thinking => "想一下…",
        OrbState.Tool => "正在查天氣…",
        OrbState.Streaming => "明天台中多雲，午後可能有短暫陣雨。",
        OrbState.Done => "明天台中多雲，午後可能有短暫陣雨，出門帶把傘。",
        OrbState.Attention => "要讀取「下載」資料夾嗎？",
        OrbState.Error => "連不上 API，檢查一下金鑰設定？",
        OrbState.Sleep => "zZ…",
        _ => null,
    };

    // ───────────────── 假對話流程（不呼叫任何 API）─────────────────
    async Task<bool> Step(int id, OrbState s, string? text, int ms)
    {
        if (id != _flowId) return false;
        Orb.SetState(s);
        ShowBubble(text);
        await Task.Delay(ms);
        return id == _flowId;
    }

    async Task RunChatDemo(string question)
    {
        int id = ++_flowId;
        if (!await Step(id, OrbState.Thinking, "想一下…", 1600)) return;
        if (!await Step(id, OrbState.Tool, "正在查資料…", 1200)) return;

        Orb.SetState(OrbState.Streaming);
        string reply = $"（POC 假回覆）你說：「{question}」。之後這裡會接上你的 BYOK 模型。";
        for (int n = 1; n <= reply.Length; n++)
        {
            if (id != _flowId) return;
            ShowBubble(reply[..n]);
            await Task.Delay(35);
        }

        if (!await Step(id, OrbState.Done, reply, 3000)) return;
        Orb.SetState(OrbState.Idle);
        ShowBubble(null);
        _idleSince = Now;
    }

    async Task RunVoiceDemo()
    {
        int id = ++_flowId;
        if (!await Step(id, OrbState.Listening, "我在聽…", 2500)) return;
        if (!await Step(id, OrbState.Transcribing, "「明天台中會下雨嗎？」", 1200)) return;
        await RunChatDemo("明天台中會下雨嗎？");
    }

    void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var q = Input.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                Input.Text = "";
                _ = RunChatDemo(q);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Input.Text = "";
            Focus();
            e.Handled = true;
        }
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            ToggleHud();
            e.Handled = true;
        }
    }

    // ───────────────── 視窗位置 ─────────────────
    void PlaceBottomRight()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var wa = screen.WorkingArea;
        double s = DesktopScaling;
        Position = new PixelPoint(
            wa.Right - (int)((Width + 24) * s),
            wa.Bottom - (int)(Height * s));
    }

    /// <summary>放開拖曳後，確保光球完整留在某一個螢幕的工作區內（多螢幕交界測試）。</summary>
    void ClampToScreen()
    {
        double s = DesktopScaling;
        var oc = Orb.TranslatePoint(Orb.Center, this) ?? new Point(Width / 2, Height / 2);
        var orbScreen = new PixelPoint(Position.X + (int)(oc.X * s), Position.Y + (int)(oc.Y * s));
        var screen = Screens.ScreenFromPoint(orbScreen) ?? Screens.Primary;
        if (screen is null) return;

        var wa = screen.WorkingArea;
        int r = (int)(Orb.BaseRadius * 1.1 * s);
        int cx = Math.Clamp(orbScreen.X, wa.X + r, wa.Right - r);
        int cy = Math.Clamp(orbScreen.Y, wa.Y + r, wa.Bottom - r);
        if (cx != orbScreen.X || cy != orbScreen.Y)
            Position = new PixelPoint(Position.X + cx - orbScreen.X, Position.Y + cy - orbScreen.Y);
    }

    // ───────────────── 光球大小 ─────────────────
    /// <summary>直徑 64 / 80 / 104。控制項為直徑的 240/104 倍，上下各吃掉一點透明邊距。</summary>
    void SetOrbSize(double diameter)
    {
        double s = diameter / 104;
        Orb.OrbDiameter = diameter;
        Orb.Width = Orb.Height = Math.Round(240 * s);
        Orb.Margin = new Thickness(0, -16 * s, 0, -16 * s);
        Orb.InvalidateVisual();
        if (IsVisible) Dispatcher.UIThread.Post(ClampToScreen, DispatcherPriority.Background);
    }

    // ───────────────── 右鍵選單 ─────────────────
    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;

        var states = new MenuItem { Header = "示範狀態" };
        foreach (var st in DemoOrder)
        {
            var item = new MenuItem { Header = StateName(st) };
            item.Click += (_, _) => SetDemoState(st);
            states.Items.Add(item);
        }

        var eyes = new MenuItem { Header = "眼睛" };
        var eyeItems = new List<(MenuItem item, EyeMode mode, string label)>();
        foreach (var (mode, label) in new[] { (EyeMode.Occasional, "偶爾現身"), (EyeMode.Always, "常駐"), (EyeMode.Off, "關閉") })
        {
            var item = new MenuItem();
            item.Click += (_, _) => Orb.EyeMode = mode;
            eyes.Items.Add(item);
            eyeItems.Add((item, mode, label));
        }

        var size = new MenuItem { Header = "大小" };
        var sizeItems = new List<(MenuItem item, double d, string label)>();
        foreach (var (d, label) in new[] { (64.0, "小（64）"), (80.0, "中（80）"), (104.0, "大（104）") })
        {
            var item = new MenuItem();
            item.Click += (_, _) => SetOrbSize(d);
            size.Items.Add(item);
            sizeItems.Add((item, d, label));
        }

        var hud = new MenuItem();
        hud.Click += (_, _) => ToggleHud();

        var clickThrough = new MenuItem();
        clickThrough.Click += (_, _) =>
        {
            _clickThroughEnabled = !_clickThroughEnabled;
            if (!_clickThroughEnabled) _plat.SetClickThrough(false);
        };

        var fps = new MenuItem();
        fps.Click += (_, _) => _adaptiveFps = !_adaptiveFps;

        var drag = new MenuItem();
        drag.Click += (_, _) => _systemDrag = !_systemDrag;

        // Emoji 測試：一般、膚色、家庭（ZWJ 組合）、國旗、ZWJ 愛心火焰
        var emoji = new MenuItem { Header = "Emoji 測試" };
        emoji.Click += (_, _) => _ = FlashBubble("一般 😀🎉  膚色 👍🏽  家庭 👨‍👩‍👧\n國旗 🇹🇼🇯🇵  組合 ❤️‍🔥 ❤️ 1️⃣\n預設彩色 ✅⭐⚡☕  文字 ✔︎ ★ ❤", 6000);

        var resetAvg = new MenuItem { Header = "重設 CPU 平均" };
        resetAvg.Click += (_, _) => _perf.ResetAverage();

        var exit = new MenuItem { Header = "結束" };
        exit.Click += (_, _) => Close();

        menu.Opening += (_, _) =>
        {
            foreach (var (item, mode, label) in eyeItems) item.Header = Check(Orb.EyeMode == mode) + label;
            foreach (var (item, d, label) in sizeItems) item.Header = Check(Math.Abs(Orb.OrbDiameter - d) < 0.5) + label;
            hud.Header = Check(Hud.IsVisible) + "診斷 HUD（F1）";
            clickThrough.Header = Check(_clickThroughEnabled) + "點擊穿透透明區域";
            clickThrough.IsEnabled = _plat.SupportsClickThrough;
            fps.Header = Check(_adaptiveFps) + "自適應幀率（休眠 12／待機 30／互動 60）";
            drag.Header = "拖曳方式：" + (_systemDrag ? "系統 BeginMoveDrag" : "手動設定 Position");
        };

        menu.Items.Add(states);
        menu.Items.Add(eyes);
        menu.Items.Add(size);
        menu.Items.Add(new Separator());
        menu.Items.Add(hud);
        menu.Items.Add(clickThrough);
        menu.Items.Add(fps);
        menu.Items.Add(drag);
        menu.Items.Add(emoji);
        menu.Items.Add(resetAvg);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    static string Check(bool on) => on ? "✓  " : "    ";

    static string StateName(OrbState s) => s switch
    {
        OrbState.Idle => "待機", OrbState.Listening => "聆聽", OrbState.Transcribing => "轉寫",
        OrbState.Thinking => "思考", OrbState.Tool => "呼叫工具", OrbState.Streaming => "回覆中",
        OrbState.Done => "完成", OrbState.Attention => "需要確認", OrbState.Error => "錯誤",
        OrbState.Sleep => "休眠", _ => s.ToString(),
    };

    // ───────────────── HUD ─────────────────
    void ToggleHud()
    {
        Hud.IsVisible = !Hud.IsVisible;
        UpdateHud();
    }

    void UpdateHud()
    {
        if (!Hud.IsVisible) return;
        string cursor = _lastCursor is { } c ? $"{c.X:0},{c.Y:0}" : "無法取得（退化為視窗內偵測）";
        string ct = !_plat.SupportsClickThrough ? "不支援"
            : !_clickThroughEnabled ? "關閉"
            : _plat.ClickThroughActive ? "穿透中" : "可互動";
        HudText.Text =
            $"{_plat.Name}\n" +
            $"FPS {_perf.Fps:0}（目標 {_frames?.TargetFps}）  縮放 {RenderScaling:0.##}/{DesktopScaling:0.##}\n" +
            $"CPU {_perf.CpuOneCore:0.0}%  30 秒平均 {_perf.CpuAvg30:0.0}%（單核）\n" +
            $"記憶體 {_perf.WorkingSetMb:0} MB\n" +
            $"狀態 {StateName(Orb.State)}  靠近 {(_near ? "是" : "否")}  拖曳 {(_dragging ? "是" : "否")}  戳 {(Orb.IsPoking ? "是" : "否")}\n" +
            $"游標 {cursor}\n" +
            $"點擊穿透 {ct}  拖曳模式 {(_systemDrag ? "系統" : "手動")}" +
            (_initError is null ? "" : $"\n{_initError}");
    }
}
