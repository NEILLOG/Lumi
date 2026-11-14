using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lumi.App.Views;

/// <summary>
/// 按住滑鼠中鍵拖曳捲動（FT-02，只在 Windows 的對話區啟用）：按下的位置是原點，游標離原點越遠捲得越快；
/// 上下捲整個訊息區，左右捲游標底下可以橫向捲動的區塊（寬表格、程式碼區塊）。放開中鍵、按任何鍵或任何滑鼠鍵就結束。
/// 按下時游標換成四向箭頭。內容本身不能捲時不攔截中鍵。
/// </summary>
internal sealed class MiddleButtonAutoScroll
{
    /// <summary>離原點這麼近以內不捲動，手微微晃動不會亂跑。</summary>
    internal const double DeadZone = 8;

    /// <summary>每一格（約 16 毫秒）捲動「超出 DeadZone 的距離 × Gain」像素，上限 <see cref="MaxStep"/>。</summary>
    internal const double Gain = 0.25;

    internal const double MaxStep = 80;

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);

    private readonly Control _host;
    private readonly ScrollViewer _vertical;
    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _timer;

    private ScrollViewer? _horizontal;
    private TopLevel? _topLevel;
    private IPointer? _pointer;
    private Cursor? _previousCursor;
    private Point _origin;
    private Point _current;

    public MiddleButtonAutoScroll(Control host, ScrollViewer vertical, Func<bool> enabled)
    {
        _host = host;
        _vertical = vertical;
        _enabled = enabled;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => Tick();

        // Tunnel：在連結、按鈕之前先收到，中鍵按在連結上也是開始捲動
        host.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        host.PointerCaptureLost += (_, _) => Stop();
        host.DetachedFromVisualTree += (_, _) => Stop();
    }

    public bool IsActive { get; private set; }

    /// <summary>超出 DeadZone 的距離換算成這一格要捲的像素（帶正負號）。</summary>
    internal static double Step(double delta)
    {
        var beyond = Math.Abs(delta) - DeadZone;
        return beyond <= 0 ? 0 : Math.Sign(delta) * Math.Min(MaxStep, beyond * Gain);
    }

    /// <summary>捲動一格。計時器每 16 毫秒呼叫一次；測試直接呼叫。</summary>
    internal void Tick()
    {
        if (!IsActive)
            return;

        var delta = _current - _origin;
        var dy = Step(delta.Y);
        if (dy != 0 && CanScrollVertically(_vertical))
            _vertical.Offset = new Vector(_vertical.Offset.X, Math.Clamp(_vertical.Offset.Y + dy, 0, MaxY(_vertical)));

        var dx = Step(delta.X);
        if (dx != 0 && _horizontal is { } h)
            h.Offset = new Vector(Math.Clamp(h.Offset.X + dx, 0, MaxX(h)), h.Offset.Y);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsActive)
        {
            // 捲動中按下任何鍵都是結束，這一下不再傳給底下的控制項
            Stop();
            e.Handled = true;
            return;
        }

        if (!_enabled() || !e.GetCurrentPoint(_host).Properties.IsMiddleButtonPressed)
            return;

        var horizontal = FindHorizontal(e.Source as Visual);
        if (!CanScrollVertically(_vertical) && horizontal is null)
            return;

        _horizontal = horizontal;
        _origin = _current = e.GetPosition(_host);
        _pointer = e.Pointer;
        _pointer.Capture(_host);
        _previousCursor = _host.Cursor;
        _host.Cursor = new Cursor(StandardCursorType.SizeAll);
        // 焦點通常在輸入框，不在訊息區：捲動期間在整個視窗聽按鍵
        _topLevel = TopLevel.GetTopLevel(_host);
        _topLevel?.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        IsActive = true;
        _timer.Start();
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (IsActive)
            _current = e.GetPosition(_host);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsActive && e.InitialPressMouseButton == MouseButton.Middle)
        {
            Stop();
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsActive)
            return;
        Stop();
        e.Handled = true;
    }

    private void Stop()
    {
        if (!IsActive)
            return;
        IsActive = false;   // 先改狀態：下面釋放 capture 會再觸發 PointerCaptureLost
        _timer.Stop();
        _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _topLevel = null;
        _host.Cursor = _previousCursor;
        _pointer?.Capture(null);
        _pointer = null;
        _horizontal = null;
    }

    /// <summary>游標底下（往外找到訊息區為止）第一個真的能橫向捲動的 ScrollViewer。</summary>
    private ScrollViewer? FindHorizontal(Visual? source)
    {
        for (var visual = source; visual is not null && visual != _vertical; visual = visual.GetVisualParent())
        {
            if (visual is ScrollViewer scroller && MaxX(scroller) > 0.5)
                return scroller;
        }
        return null;
    }

    private static bool CanScrollVertically(ScrollViewer scroller) => MaxY(scroller) > 0.5;

    private static double MaxY(ScrollViewer s) => Math.Max(0, s.Extent.Height - s.Viewport.Height);

    private static double MaxX(ScrollViewer s) => Math.Max(0, s.Extent.Width - s.Viewport.Width);
}
